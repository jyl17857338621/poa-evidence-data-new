using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;

namespace PoaNet;

public static class Auth
{
    private static string UsersFile => Path.Combine(Db.DataDir, "users.json");
    private static string SecretFile => Path.Combine(Db.DataDir, ".session_secret");

    public static List<UserRec> Users { get; set; } = new();

    private static string _secret = "";
    public static string Secret => _secret;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static void Init()
    {
        LoadSecret();
        LoadUsers();
    }

    private static void LoadSecret()
    {
        try
        {
            if (File.Exists(SecretFile)) { _secret = File.ReadAllText(SecretFile).Trim(); return; }
        }
        catch { }
        _secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        try { File.WriteAllText(SecretFile, _secret); } catch { }
    }

    private static void LoadUsers()
    {
        if (File.Exists(UsersFile))
        {
            try { Users = JsonSerializer.Deserialize<List<UserRec>>(File.ReadAllText(UsersFile)) ?? new(); }
            catch { Users = new(); }
        }
        if (Users == null || Users.Count == 0)
        {
            var u = (Environment.GetEnvironmentVariable("ADMIN_USER") ?? "SLZZ888").Trim();
            var p = Environment.GetEnvironmentVariable("ADMIN_PASSWORD") ?? "yunyingsanbu888";
            Users = new List<UserRec> { CreateUser(u, p, "admin") };
            SaveUsers();
            Console.WriteLine($"【首次启动】已创建管理员账号: {u} (可用 ADMIN_USER / ADMIN_PASSWORD 环境变量覆盖)");
        }
    }

    public static void SaveUsers()
    {
        var tmp = UsersFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Users, JsonOpts));
        File.Move(tmp, UsersFile, true);
    }

    // 对齐 Node crypto.scryptSync(password, salt, 64): salt 为 hex 字符串,Node 取其 UTF-8 字节作为实际 salt
    public static byte[] ScryptDerive(string password, string saltHexString, int keylen = 64)
    {
        var pw = Encoding.UTF8.GetBytes(password);
        var salt = Encoding.UTF8.GetBytes(saltHexString);
        return Scrypt.Derive(pw, salt, 16384, 8, 1, keylen);
    }

    public static string HashPassword(string password, string salt)
        => Convert.ToHexString(ScryptDerive(password, salt, 64)).ToLowerInvariant();

    public static UserRec CreateUser(string username, string password, string role)
    {
        var salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        return new UserRec
        {
            username = username,
            salt = salt,
            hash = HashPassword(password, salt),
            role = role ?? "editor",
            createdAt = DateTime.UtcNow.ToString("o")
        };
    }

    public static bool VerifyPassword(string password, UserRec u)
    {
        if (u == null) return false;
        var h = HashPassword(password, u.salt);
        if (h.Length != u.hash.Length) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(h), Encoding.UTF8.GetBytes(u.hash));
    }

    private static string B64Url(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string DecodeB64Url(string s)
    {
        var t = s.Replace('-', '+').Replace('_', '/');
        while (t.Length % 4 != 0) t += "=";
        return Encoding.UTF8.GetString(Convert.FromBase64String(t));
    }

    // Cookie 签名使用 base64url（RFC 4648 §5），不含 '+'、'/'、'=' 等会被 ASP.NET
    // 在 Set-Cookie 时转义、读取时把 '+' 误判为空格的字符，从而避免验签失败。
    private static string Sign(string username)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_secret));
        return B64Url(hmac.ComputeHash(Encoding.UTF8.GetBytes(username)));
    }

    public static string MakeCookie(string username)
        => B64Url(Encoding.UTF8.GetBytes(username)) + "." + Sign(username);

    public static string? GetUser(HttpContext ctx)
    {
        if (!ctx.Request.Cookies.TryGetValue("sid", out var c) || string.IsNullOrEmpty(c)) return null;
        var i = c.IndexOf('.');
        if (i < 0) return null;
        var user = DecodeB64Url(c[..i]);
        var sig = c[(i + 1)..];
        if (Sign(user) != sig) return null;
        if (!Users.Any(x => x.username == user)) return null;
        return user;
    }

    public static bool IsAdmin(string? user) => user != null && Users.Any(x => x.username == user && x.role == "admin");
}
