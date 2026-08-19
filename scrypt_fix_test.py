import hashlib, hmac, struct

def pbkdf2(password, salt, c, dklen):
    out = b''
    for i in range(1, (dklen+31)//32 + 1):
        u = hmac.new(password, salt + struct.pack('>I', i), hashlib.sha256).digest()
        t = bytearray(u)
        for _ in range(2, c+1):
            u = hmac.new(password, u, hashlib.sha256).digest()
            for k in range(32): t[k] ^= u[k]
        out += bytes(t)
    return out[:dklen]

def rotl(v, c):
    v &= 0xffffffff
    return ((v << c) | (v >> (32 - c))) & 0xffffffff

def qr(x, a, b, c, d):
    x[b] ^= rotl((x[a]+x[d]) & 0xffffffff, 7)
    x[c] ^= rotl((x[b]+x[a]) & 0xffffffff, 9)
    x[d] ^= rotl((x[c]+x[b]) & 0xffffffff, 13)
    x[a] ^= rotl((x[d]+x[c]) & 0xffffffff, 18)

def salsa20_8(inp):
    x = list(struct.unpack('<16I', inp))
    orig = x[:]
    for _ in range(4):
        qr(x,0,4,8,12); qr(x,5,9,13,1); qr(x,10,14,2,6); qr(x,15,3,7,11)
        qr(x,0,1,2,3); qr(x,5,6,7,4); qr(x,10,11,8,9); qr(x,15,12,13,14)
    return b''.join(struct.pack('<I', (x[i]+orig[i]) & 0xffffffff) for i in range(16))

def blockmix(B, r, interleave):
    blocks = 2*r
    Y = [None]*blocks
    X = B[(blocks-1)*64 : blocks*64]
    for i in range(blocks):
        T = bytes(a^b for a,b in zip(X, B[i*64:(i+1)*64]))
        X = salsa20_8(T)
        Y[i] = X
    if interleave:
        out = b''
        for i in range(0, blocks, 2): out += Y[i]   # even blocks first
        for i in range(1, blocks, 2): out += Y[i]   # odd blocks second
        return out
    return b''.join(Y)

def integerify(X, r):
    off = 128*r - 64
    return int.from_bytes(X[off:off+8], 'little')

def smix(block, N, r, interleave):
    chunk = 128*r
    V = [None]*N
    X = bytearray(block)
    for i in range(N):
        V[i] = bytes(X)
        X = bytearray(blockmix(bytes(X), r, interleave))
    for i in range(N):
        j = integerify(X, r) % N
        T = bytearray(len(X))
        for k in range(len(X)): T[k] = X[k] ^ V[j][k]
        X = bytearray(blockmix(bytes(T), r, interleave))
    return bytes(X)

def scrypt(password, salt, N, r, dklen, interleave):
    B = bytearray(pbkdf2(password, salt, 1, 1*128*r))
    chunk = smix(B, N, r, interleave)
    return pbkdf2(password, bytes(chunk), 1, dklen)

vecs = [
    ("", "", 16, 1, "77d6576238657b203b19ca42c18a0497f16b4844e3074ae8dfdffa3fede21442fcd0069ded0948f8326a753a0fc81f17e8d3e0fb2e0d3628cf35e20c38d18906"),
    ("password", "NaCl", 1024, 8, "27b418c674c769d12501fbb1f53bac32df6514c0f28d043872b148b348961a79057a6861cc3553246aa0ddb63bc074450b924022547a799538d603396835dd62"),
    ("password", "NaCl", 1024, 1, "8bb740a753619bbb66185549639d5f540396aea07bbd123032197014c28f8affc96ba38bddfff4fa68e93d297297479eb686f70f821450efb3f9aaa550336a6a"),
    ("password", "NaCl", 16, 8, "f5dfb3972e7908b22410c5c5f3788907cdbd1a79971b12277502bd4a77e6d5d3a7ffd9f9969c38c865446d1a8053d0e94f08086ee3c72950387be3b3a9716f9b"),
    ("yunyingsanbu888", "d8d13d74329720a0a69f14ef6222204a", 16384, 8, "cc426c8f5db9b30242458387bc742a88a681fc6529730c76a9fb3d0ac62122c32c078b4789093817d0b1e5d78287c6549aaaac6ef8d652c0fdfd0316205583f3"),
]
for inter in (False, True):
    print(f"=== interleave={inter} ===")
    allok = True
    for pw, salt, N, r, exp in vecs:
        got = scrypt(pw.encode(), salt.encode(), N, r, 64, inter).hex()
        ok = got == exp
        allok &= ok
        print(f"  N={N} r={r} match={ok}")
    print("  ALL OK:", allok)
