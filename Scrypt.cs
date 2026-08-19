using System;
using System.Security.Cryptography;

namespace PoaNet;

/// <summary>
/// 纯 C# 实现的 scrypt (RFC 7914)，与 Node 的 crypto.scryptSync 字节级兼容。
/// 不依赖任何第三方包，保证离线可构建、且能校验 Node 端已存储的账号密码。
/// 参数：N=16384, r=8, p=1, 输出 64 字节（与 Node 默认一致）。
/// </summary>
public static class Scrypt
{
    // salt 直接以原始字节传入（调用方负责把 hex 字符串按 UTF-8 编码为字节，以对齐 Node 行为）
    public static byte[] Derive(byte[] password, byte[] salt, int N, int r, int p, int dkLen)
    {
        if (N <= 1 || (N & (N - 1)) != 0) throw new ArgumentException("N 必须是 2 的幂且 > 1", nameof(N));
        if (r <= 0 || p <= 0) throw new ArgumentException("r/p 必须 > 0");

        // 1) 用 PBKDF2-HMAC-SHA256(c=1) 派生初值 B，长度 = p * 128 * r
        int blockLen = p * 128 * r;
        byte[] B = Pbkdf2Sha256(password, salt, 1, blockLen);

        // 2) 对每个 p 块做 ROMix(SMix)
        int chunkLen = 128 * r; // 每块字节数
        for (int i = 0; i < p; i++)
        {
            byte[] chunk = new byte[chunkLen];
            Array.Copy(B, i * chunkLen, chunk, 0, chunkLen);
            Smix(chunk, N, r);
            Array.Copy(chunk, 0, B, i * chunkLen, chunkLen);
        }

        // 3) PBKDF2-HMAC-SHA256(password, B, c=1, dkLen)
        return Pbkdf2Sha256(password, B, 1, dkLen);
    }

    private static void Smix(byte[] block, int N, int r)
    {
        int chunkLen = 128 * r;
        byte[][] V = new byte[N][];
        byte[] X = (byte[])block.Clone();

        for (int i = 0; i < N; i++)
        {
            V[i] = (byte[])X.Clone();
            BlockMix(X, r);
        }
        for (int i = 0; i < N; i++)
        {
            int jIdx = (int)(Integerify(X, r) % (ulong)N);
            XorInto(X, V[jIdx]);
            BlockMix(X, r);
        }
        Array.Copy(X, 0, block, 0, chunkLen);
    }

    private static ulong Integerify(byte[] X, int r)
    {
        // 取最后一块（索引 2r-1）的前 8 字节，小端解释为 64 位无符号整数
        int off = 128 * r - 64;
        ulong val = 0;
        for (int k = 0; k < 8; k++)
            val |= (ulong)X[off + k] << (8 * k);
        return val;
    }

    private static void XorInto(byte[] X, byte[] Vj)
    {
        for (int i = 0; i < X.Length; i++)
            X[i] ^= Vj[i];
    }

    private static void BlockMix(byte[] B, int r)
    {
        int blocks = 2 * r; // 64 字节块数量
        byte[][] Y = new byte[blocks][];
        byte[] X = new byte[64];
        Array.Copy(B, (blocks - 1) * 64, X, 0, 64);
        for (int i = 0; i < blocks; i++)
        {
            byte[] T = new byte[64];
            for (int k = 0; k < 64; k++)
                T[k] = (byte)(X[k] ^ B[i * 64 + k]);
            X = Salsa208(T);
            Y[i] = (byte[])X.Clone();
        }
        // RFC 7914 scryptBlockMix 步骤 3:
        //   B' = (Y[0], Y[2], ..., Y[2r-2], Y[1], Y[3], ..., Y[2r-1])
        // 即偶数索引块在前、奇数索引块在后。r=1 时两种排列等价，
        // 因此此前 r=1 通过而 r=8 失败的根因就在这一重组步骤。
        int off = 0;
        for (int i = 0; i < blocks; i += 2) { Array.Copy(Y[i], 0, B, off, 64); off += 64; }
        for (int i = 1; i < blocks; i += 2) { Array.Copy(Y[i], 0, B, off, 64); off += 64; }
    }

    // Salsa20/8 核心：对 64 字节输入做 8 轮（4 个 column+row 双轮），输出 64 字节。
    // 端口自 scrypt 参考实现的 salsa208_word_specification。
    private static byte[] Salsa208(byte[] input)
    {
        uint[] x = new uint[16];
        uint[] indata = new uint[16];
        for (int i = 0; i < 16; i++)
            indata[i] = x[i] = ReadLE(input, i * 4);

        for (int round = 0; round < 8; round += 2) // Salsa20/8 = 4 个双轮（column+row 各 4 次）
        {
            // column rounds
            QR(ref x[0], ref x[4], ref x[8], ref x[12]);
            QR(ref x[5], ref x[9], ref x[13], ref x[1]);
            QR(ref x[10], ref x[14], ref x[2], ref x[6]);
            QR(ref x[15], ref x[3], ref x[7], ref x[11]);
            // row rounds
            QR(ref x[0], ref x[1], ref x[2], ref x[3]);
            QR(ref x[5], ref x[6], ref x[7], ref x[4]);
            QR(ref x[10], ref x[11], ref x[8], ref x[9]);
            QR(ref x[15], ref x[12], ref x[13], ref x[14]);
        }

        byte[] output = new byte[64];
        for (int i = 0; i < 16; i++)
        {
            uint val = x[i] + indata[i];
            output[i * 4 + 0] = (byte)val;
            output[i * 4 + 1] = (byte)(val >> 8);
            output[i * 4 + 2] = (byte)(val >> 16);
            output[i * 4 + 3] = (byte)(val >> 24);
        }
        return output;
    }

    private static void QR(ref uint a, ref uint b, ref uint c, ref uint d)
    {
        b ^= Rotl(a + d, 7);
        c ^= Rotl(b + a, 9);
        d ^= Rotl(c + b, 13);
        a ^= Rotl(d + c, 18);
    }

    private static uint Rotl(uint v, int c) => (v << c) | (v >> (32 - c));

    private static uint ReadLE(byte[] buf, int off)
        => (uint)(buf[off] | (buf[off + 1] << 8) | (buf[off + 2] << 16) | (buf[off + 3] << 24));

    public static byte[] Pbkdf2Sha256(byte[] password, byte[] salt, int c, int dkLen)
    {
        using var hmac = new HMACSHA256(password);
        int hLen = 32;
        int blocks = (dkLen + hLen - 1) / hLen;
        byte[] result = new byte[dkLen];
        byte[] intBuf = new byte[4];
        for (int i = 1; i <= blocks; i++)
        {
            intBuf[0] = (byte)(i >> 24);
            intBuf[1] = (byte)(i >> 16);
            intBuf[2] = (byte)(i >> 8);
            intBuf[3] = (byte)i;
            byte[] u = hmac.ComputeHash(Concat(salt, intBuf));
            byte[] t = (byte[])u.Clone();
            for (int n = 2; n <= c; n++)
            {
                u = hmac.ComputeHash(u);
                for (int k = 0; k < t.Length; k++) t[k] ^= u[k];
            }
            int outOff = (i - 1) * hLen;
            int take = Math.Min(hLen, dkLen - outOff);
            Array.Copy(t, 0, result, outOff, take);
        }
        return result;
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        byte[] r = new byte[a.Length + b.Length];
        Array.Copy(a, 0, r, 0, a.Length);
        Array.Copy(b, 0, r, a.Length, b.Length);
        return r;
    }
}
