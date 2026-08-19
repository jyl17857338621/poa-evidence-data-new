import hashlib, hmac, struct

def pbkdf2(password, salt, c, dklen):
    hlen = 32
    nblocks = (dklen + hlen - 1) // hlen
    out = b''
    for i in range(1, nblocks+1):
        u = hmac.new(password, salt + struct.pack('>I', i), hashlib.sha256).digest()
        t = bytearray(u)
        for _ in range(2, c+1):
            u = hmac.new(password, u, hashlib.sha256).digest()
            for k in range(32):
                t[k] ^= u[k]
        out += bytes(t)
    return out[:dklen]

def rotl(v, c):
    v &= 0xffffffff
    return ((v << c) | (v >> (32 - c))) & 0xffffffff

def qr(x, a, b, c, d):
    x[b] ^= rotl((x[a] + x[d]) & 0xffffffff, 7)
    x[c] ^= rotl((x[b] + x[a]) & 0xffffffff, 9)
    x[d] ^= rotl((x[c] + x[b]) & 0xffffffff, 13)
    x[a] ^= rotl((x[d] + x[c]) & 0xffffffff, 18)

def salsa20_8(inp):
    x = list(struct.unpack('<16I', inp))
    orig = x[:]
    for _ in range(4):
        qr(x, 0,4,8,12); qr(x, 5,9,13,1); qr(x, 10,14,2,6); qr(x, 15,3,7,11)
        qr(x, 0,1,2,3); qr(x, 5,6,7,4); qr(x, 10,11,8,9); qr(x, 15,12,13,14)
    return b''.join(struct.pack('<I', (x[i] + orig[i]) & 0xffffffff) for i in range(16))

def blockmix(B, r):
    blocks = 2*r
    Y = [None]*blocks
    X = B[(blocks-1)*64 : blocks*64]
    for i in range(blocks):
        T = bytes(a ^ b for a, b in zip(X, B[i*64:(i+1)*64]))
        X = salsa20_8(T)
        Y[i] = X
    return b''.join(Y)

def integerify(X, r, off):
    return int.from_bytes(X[off:off+8], 'little')

def smix(block, N, r, off):
    chunk = 128*r
    V = [None]*N
    X = bytearray(block)
    for i in range(N):
        V[i] = bytes(X)
        X = bytearray(blockmix(bytes(X), r))
    for i in range(N):
        j = integerify(X, r, off) % N
        T = bytearray(len(X))
        for k in range(len(X)):
            T[k] = X[k] ^ V[j][k]
        X = bytearray(blockmix(bytes(T), r))
    return bytes(X)

def scrypt(password, salt, N, r, p, dklen, off):
    blocklen = p*128*r
    B = bytearray(pbkdf2(password, salt, 1, blocklen))
    for i in range(p):
        chunk = B[i*128*r:(i+1)*128*r]
        chunk = smix(chunk, N, r, off)
        B[i*128*r:(i+1)*128*r] = chunk
    return pbkdf2(password, bytes(B), 1, dklen)

vectors = [
    ("", "", 16, 1, 1, "77d6576238657b203b19ca42c18a0497f16b4844e3074ae8dfdffa3fede21442fcd0069ded0948f8326a753a0fc81f17e8d3e0fb2e0d3628cf35e20c38d18906"),
    ("password", "NaCl", 1024, 8, 1, "27b418c674c769d12501fbb1f53bac32df6514c0f28d043872b148b348961a79057a6861cc3553246aa0ddb63bc074450b924022547a799538d603396835dd62"),
    ("password", "NaCl", 1024, 1, 1, "8bb740a753619bbb66185549639d5f540396aea07bbd123032197014c28f8affc96ba38bddfff4fa68e93d297297479eb686f70f821450efb3f9aaa550336a6a"),
    ("password", "NaCl", 16, 8, 1, "f5dfb3972e7908b22410c5c5f3788907cdbd1a79971b12277502bd4a77e6d5d3a7ffd9f9969c38c865446d1a8053d0e94f08086ee3c72950387be3b3a9716f9b"),
]
for off in (128*8-64, 128*8-8, 64):
    print("=== Integerify offset", off, "===")
    for pw, salt, N, r, p, exp in vectors:
        got = scrypt(pw.encode(), salt.encode(), N, r, p, 64, off).hex()
        ok = got == exp
        if r == 8 or (r==1 and N==16):
            print(f"  N={N} r={r} match={ok}")
