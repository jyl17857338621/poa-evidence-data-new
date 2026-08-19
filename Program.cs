using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.ResponseCompression;

namespace PoaNet;

public class Program
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public static void Main(string[] args)
    {
        // 自测模式：以 Node crypto.scryptSync / crypto.pbkdf2Sync 为事实基准（users.json 由 Node 哈希）
        if (args.Length > 0 && args[0] == "--selftest")
        {
            var pbk1 = Convert.ToHexString(Scrypt.Pbkdf2Sha256(Encoding.UTF8.GetBytes("password"), Encoding.UTF8.GetBytes("salt"), 1, 32)).ToLowerInvariant();
            var pbk2 = Convert.ToHexString(Scrypt.Pbkdf2Sha256(Encoding.UTF8.GetBytes(""), Encoding.UTF8.GetBytes(""), 1, 128)).ToLowerInvariant();
            Console.WriteLine("[PBKDF2] password/salt/1/32 match=" + (pbk1 == "120fb6cffcf8b32c43e7225256c4f837a86548c92ccc35480805987cb70be17b"));
            Console.WriteLine("[PBKDF2] empty/empty/1/128 match=" + (pbk2 == "f7ce0b653d2d72a4108cf5abe912ffdd777616dbbb27a70e8204f3ae2d0f6fad89f68f4811d1e87bcc3bd7400a9ffd29094f0184639574f39ae5a1315217bcd7894991447213bb226c25b54da86370fbcd984380374666bb8ffcb5bf40c254b067d27c51ce4ad5fed829c90b505a571b7f4d1cad6a523cda770e67bceaaf7e89"));

            var vectors = new (string pw, string salt, int N, int r, int p, string exp)[]
            {
                ("", "", 16, 1, 1, "77d6576238657b203b19ca42c18a0497f16b4844e3074ae8dfdffa3fede21442fcd0069ded0948f8326a753a0fc81f17e8d3e0fb2e0d3628cf35e20c38d18906"),
                ("password", "NaCl", 1024, 8, 1, "27b418c674c769d12501fbb1f53bac32df6514c0f28d043872b148b348961a79057a6861cc3553246aa0ddb63bc074450b924022547a799538d603396835dd62"),
                ("password", "NaCl", 1024, 1, 1, "8bb740a753619bbb66185549639d5f540396aea07bbd123032197014c28f8affc96ba38bddfff4fa68e93d297297479eb686f70f821450efb3f9aaa550336a6a"),
                ("password", "NaCl", 16, 8, 1, "f5dfb3972e7908b22410c5c5f3788907cdbd1a79971b12277502bd4a77e6d5d3a7ffd9f9969c38c865446d1a8053d0e94f08086ee3c72950387be3b3a9716f9b"),
            };
            foreach (var v in vectors)
            {
                var hex = Convert.ToHexString(Scrypt.Derive(Encoding.UTF8.GetBytes(v.pw), Encoding.UTF8.GetBytes(v.salt), v.N, v.r, v.p, 64)).ToLowerInvariant();
                Console.WriteLine($"[SCRYPT] N={v.N} r={v.r} match={hex == v.exp} hex={hex}");
            }
            var pw = "yunyingsanbu888";
            var salt = "d8d13d74329720a0a69f14ef6222204a";
            var real = Convert.ToHexString(Auth.ScryptDerive(pw, salt, 64)).ToLowerInvariant();
            var realExp = "cc426c8f5db9b30242458387bc742a88a681fc6529730c76a9fb3d0ac62122c32c078b4789093817d0b1e5d78287c6549aaaac6ef8d652c0fdfd0316205583f3";
            Console.WriteLine("[ADMIN] match=" + (real == realExp));
            return;
        }

        var builder = WebApplication.CreateBuilder(args);
        var port = Environment.GetEnvironmentVariable("PORT");
        var listenPort = string.IsNullOrEmpty(port) ? 3000 : int.Parse(port);
        builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 300L * 1024 * 1024); // 300MB
        builder.WebHost.UseUrls($"http://0.0.0.0:{listenPort}");
        builder.Services.AddResponseCompression(o => { o.Providers.Add<GzipCompressionProvider>(); o.MimeTypes = new[] { "text/plain", "text/html", "text/css", "application/javascript", "application/json", "image/png", "image/jpeg", "image/webp", "application/pdf", "application/xml" }; });
        var app = builder.Build();
        app.UseResponseCompression();

        // 目录：默认指向现有 amazon-poa-evidence 的 data/uploads/public（零数据重复，作后端替换）
        var baseDir = AppContext.BaseDirectory;
        var root = Environment.GetEnvironmentVariable("POA_ROOT");
        string dataDir, uploadsDir, publicDir;
        if (!string.IsNullOrEmpty(root))
        {
            dataDir = Path.Combine(root, "data");
            uploadsDir = Path.Combine(root, "uploads");
            publicDir = Path.Combine(root, "public");
        }
        else
        {
            // 回退：指向兄弟目录 amazon-poa-evidence（按可执行文件位置推导）
            var sibling = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "amazon-poa-evidence"));
            if (!Directory.Exists(sibling)) sibling = Path.GetFullPath(Path.Combine(baseDir, "..", "amazon-poa-evidence"));
            dataDir = Path.Combine(sibling, "data");
            uploadsDir = Path.Combine(sibling, "uploads");
            publicDir = Path.Combine(sibling, "public");
        }

        Db.Init(dataDir, uploadsDir, publicDir);
        Auth.Init();
        Db.BackupOnce();

        // 启动日志放在 ApplicationStarted 生命周期事件中（此时端口已真正绑定）
        app.Lifetime.ApplicationStarted.Register(() =>
            Console.WriteLine($"亚马逊美国站 POA 证据库(.NET)已启动: http://localhost:{listenPort}"));

        // 注册终端中间件（处理所有尚未匹配的请求）
        app.Run(async ctx => await Dispatch(ctx, publicDir));

        // 关键：启动 Kestrel 主机（阻塞）。注意 app.Run(...) 仅注册中间件，不会真正开始监听。
        app.Run();
    }

    // ============ 分发 ============
    private static async Task Dispatch(HttpContext ctx, string publicDir)
    {
        var path = ctx.Request.Path.ToString();
        try
        {
            if (path == "/" || path == "/index.html")
            {
                var idx = Path.Combine(publicDir, "index.html");
                if (!File.Exists(idx)) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsync("index.html 未找到: " + idx); return; }
                ctx.Response.Headers["Content-Type"] = "text/html; charset=utf-8";
                ctx.Response.Headers["Cache-Control"] = "no-cache";
                await ctx.Response.SendFileAsync(idx);
                return;
            }
            if (path.StartsWith("/uploads/", StringComparison.OrdinalIgnoreCase))
            {
                var user = Auth.GetUser(ctx);
                if (user == null) { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsJsonAsync(new { error = "需要登录", loginRequired = true }); return; }
                var fp = Files.ResolveUnderUploads(path);
                if (fp == null || !File.Exists(fp)) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsync("not found"); return; }
                ctx.Response.Headers["Content-Type"] = Files.MimeFor(Path.GetExtension(fp).ToLowerInvariant());
                await ctx.Response.SendFileAsync(fp);
                return;
            }
            if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            {
                await HandleApi(ctx, path);
                return;
            }
            ctx.Response.StatusCode = 404;
            await ctx.Response.WriteAsync("not found");
        }
        catch (Exception e)
        {
            ctx.Response.StatusCode = 500;
            await ctx.Response.WriteAsJsonAsync(new { error = e.Message });
        }
    }

    // ============ API ============
    private static async Task HandleApi(HttpContext ctx, string path)
    {
        var method = ctx.Request.Method;
        var q = ctx.Request.Query;

        // 登录（无需鉴权）
        if (path == "/api/login" && method == "POST")
        {
            var b = await ReadJson(ctx);
            var username = JStr(b, "username").Trim();
            var password = JStr(b, "password");
            var u = Auth.Users.FirstOrDefault(x => x.username == username);
            if (u != null && Auth.VerifyPassword(password, u))
            {
                var cookie = Auth.MakeCookie(u.username);
                ctx.Response.Cookies.Append("sid", cookie, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax, Path = "/", MaxAge = TimeSpan.FromSeconds(864000) });
                await SendJson(ctx, 200, new { ok = true, username = u.username });
            }
            else
            {
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsJsonAsync(new { error = "账号或密码错误" });
            }
            return;
        }
        if (path == "/api/logout" && method == "POST")
        {
            ctx.Response.Cookies.Append("sid", "", new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax, Path = "/", MaxAge = TimeSpan.Zero });
            await SendJson(ctx, 200, new { ok = true });
            return;
        }

        var user = Auth.GetUser(ctx);
        if (user == null) { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsJsonAsync(new { error = "需要登录", loginRequired = true }); return; }

        if (path == "/api/me" && method == "GET")
        {
            var me = Auth.Users.First(x => x.username == user);
            await SendJson(ctx, 200, new { username = me.username, role = me.role });
            return;
        }

        // ===== 账号管理 =====
        if (path == "/api/users" && method == "GET")
        {
            if (!Auth.IsAdmin(user)) { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsJsonAsync(new { error = "需要管理员权限" }); return; }
            await SendJson(ctx, 200, Auth.Users.Select(u => new { u.username, u.role, u.createdAt }).ToArray());
            return;
        }
        if (path == "/api/users" && method == "POST")
        {
            if (!Auth.IsAdmin(user)) { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsJsonAsync(new { error = "需要管理员权限" }); return; }
            var b = await ReadJson(ctx);
            var username = JStr(b, "username").Trim();
            var password = JStr(b, "password");
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password)) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { error = "username 与 password 必填" }); return; }
            if (Auth.Users.Any(x => x.username == username)) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { error = "账号已存在" }); return; }
            var role = JStr(b, "role") == "admin" ? "admin" : "editor";
            Auth.Users.Add(Auth.CreateUser(username, password, role));
            Auth.SaveUsers();
            ctx.Response.StatusCode = 201;
            await SendJson(ctx, 201, new { username, role });
            return;
        }
        var mUser = RegexMatch(path, @"^/api/users/([\w]+)$");
        if (mUser != null)
        {
            var target = mUser[1];
            var meU = Auth.Users.First(x => x.username == user);
            if (method == "PUT")
            {
                var isSelf = meU.username == target;
                if (!isSelf && meU.role != "admin") { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsJsonAsync(new { error = "无权限" }); return; }
                var u = Auth.Users.FirstOrDefault(x => x.username == target);
                if (u == null) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsJsonAsync(new { error = "not found" }); return; }
                var b = await ReadJson(ctx);
                if (!isSelf && meU.role != "admin")
                {
                    var old = JStr(b, "oldPassword");
                    if (Auth.HashPassword(old, u.salt) != u.hash) { ctx.Response.StatusCode = 401; await ctx.Response.WriteAsJsonAsync(new { error = "原密码错误" }); return; }
                }
                var np = JStr(b, "newPassword");
                if (string.IsNullOrEmpty(np)) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { error = "newPassword 必填" }); return; }
                u.salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
                u.hash = Auth.HashPassword(np, u.salt);
                u.updatedAt = DateTime.UtcNow.ToString("o");
                Auth.SaveUsers();
                await SendJson(ctx, 200, new { ok = true });
                return;
            }
            if (method == "DELETE")
            {
                if (meU.role != "admin") { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsJsonAsync(new { error = "需要管理员权限" }); return; }
                if (meU.username == target) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { error = "不能删除自己" }); return; }
                Auth.Users = Auth.Users.Where(x => x.username != target).ToList();
                Auth.SaveUsers();
                await SendJson(ctx, 200, new { ok = true });
                return;
            }
        }

        // 统计
        if (path == "/api/stats" && method == "GET")
        {
            int media = 0;
            var byStatus = new Dictionary<string, int>();
            var counted = new HashSet<string>();
            foreach (var p in Db.Model.products)
            {
                var spu = string.IsNullOrEmpty(p.spuId) ? null : Db.Model.spus.FirstOrDefault(s => s.id == p.spuId);
                int spuMedia = 0;
                if (spu != null && !counted.Contains(spu.id)) { counted.Add(spu.id); spuMedia = spu.manuals?.Count ?? 0; }
                media += p.packaging.Count * 2 + p.dims.photos.Count + p.warehouse.Count + spuMedia;
                var st = p.status ?? "待调查";
                byStatus[st] = (byStatus.ContainsKey(st) ? byStatus[st] : 0) + 1;
            }
            await SendJson(ctx, 200, new { products = Db.Model.products.Count, spus = Db.Model.spus.Count, boards = Db.Model.boards.Count, media, byStatus });
            return;
        }

        // 扁平化素材
        if (path == "/api/photos" && method == "GET")
        {
            var outList = new List<PhotoFlat>();
            var seen = new HashSet<string>();
            void Push(Product p, string section, string kind, string? src, string note, string? preview)
            {
                if (string.IsNullOrEmpty(src)) return;
                var spu = string.IsNullOrEmpty(p.spuId) ? null : Db.Model.spus.FirstOrDefault(s => s.id == p.spuId);
                var board = string.IsNullOrEmpty(p.boardId) ? null : Db.Model.boards.FirstOrDefault(b => b.id == p.boardId);
                outList.Add(new PhotoFlat
                {
                    productId = p.id, sku = p.sku, productName = p.productName, status = p.status, section = section, kind = kind, src = src, note = note ?? "",
                    preview = preview ?? "", spuId = spu?.id, spuCode = spu?.code ?? "", spuName = spu?.name ?? "",
                    boardId = board?.id, boardName = board?.name ?? ""
                });
            }
            foreach (var p in Db.Model.products)
            {
                foreach (var e in p.packaging) { Push(p, "内外包装对比", e.kind + " 改进前", e.before, e.note, null); Push(p, "内外包装对比", e.kind + " 改进后", e.after, e.note, null); }
                var sm = (string.IsNullOrEmpty(p.spuId) ? null : Db.Model.spus.FirstOrDefault(s => s.id == p.spuId))?.manuals ?? new List<Manual>();
                foreach (var m in sm)
                {
                    var key = (p.spuId ?? p.id) + "|" + m.id;
                    if (seen.Contains(key)) continue;
                    seen.Add(key);
                    var label = m.kind == "accessory" ? "配件说明书" : "安装说明书";
                    Push(p, label, label, m.src, m.note, m.preview);
                }
                foreach (var e in p.dims.photos) Push(p, "尺寸图", "尺寸图", e.src, e.note, null);
                foreach (var e in p.warehouse) Push(p, "工厂/托盘照片", "工厂/托盘照片", e.src, e.note, null);
            }
            await SendJson(ctx, 200, outList);
            return;
        }

        // 导入模板
        if (path == "/api/import-template" && method == "GET")
        {
            var header = new[] { "SKU", "产品名称", "版块", "一级分类", "二级分类", "三级分类", "产品系列编码(SPU)", "调查原因", "状态", "产品长(in)", "产品宽(in)", "产品高(in)", "产品重(lb)", "外包装长(in)", "外包装宽(in)", "外包装高(in)", "外包装重(lb)" };
            var sample = new[] { "", "便携蓝牙音箱", "音响版块", "电子产品", "音响设备", "蓝牙音箱", "SPU-SPK-01", "外包装无英文警示语", "待调查", "3.94", "1.97", "1.18", "0.44", "", "", "", "3.31" };
            var csv = string.Join(",", header) + "\n" + string.Join(",", sample);
            var buf = Encoding.UTF8.GetBytes("\uFEFF" + csv);
            var fname = "POA_import_template.csv";
            ctx.Response.Headers["Content-Type"] = "text/csv; charset=utf-8";
            ctx.Response.Headers["Content-Disposition"] = $"attachment; filename=\"{fname}\"; filename*=UTF-8''{Uri.EscapeDataString(fname)}";
            await ctx.Response.Body.WriteAsync(buf, 0, buf.Length);
            return;
        }

        // 批量导入
        if (path == "/api/import" && method == "POST")
        {
            List<List<string>>? rows = null;
            var ct = (ctx.Request.ContentType ?? "").ToLowerInvariant();
            try
            {
                if (ct.Contains("application/json"))
                {
                    var b = await ReadJson(ctx);
                    var fname = JStr(b, "filename").ToLowerInvariant();
                    var content = JStr(b, "content");
                    if (fname.EndsWith(".xlsx"))
                    {
                        var bytes = Convert.FromBase64String(content);
                        rows = await ParseXlsx(bytes);
                    }
                    else rows = ParseCsv(DecodeBase64String(content));
                }
                else
                {
                    var text = await ReadBodyString(ctx);
                    rows = ParseCsv(text);
                }
            }
            catch (Exception e) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { error = "解析失败: " + e.Message }); return; }
            if (rows == null || rows.Count < 2) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { error = "文件为空或缺少数据行" }); return; }
            var result = RunImport(rows);
            await SendJson(ctx, 200, result);
            return;
        }

        // 上传(base64)
        if (path == "/api/upload" && method == "POST")
        {
            var b = await ReadJson(ctx);
            var pid = JStr(b, "productId");
            var prod = Db.Model.products.FirstOrDefault(p => p.id == pid);
            if (prod == null) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { error = "invalid productId" }); return; }
            var r = Files.SaveBase64(JStr(b, "dataUrl"), prod.id);
            if (!r.ok) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { error = r.err }); return; }
            await SendJson(ctx, 200, new { src = r.src });
            return;
        }
        // 上传(原始字节)
        if (path == "/api/upload/raw" && method == "POST")
        {
            var pid = q["productId"].ToString();
            var prod = Db.Model.products.FirstOrDefault(p => p.id == pid);
            if (prod == null) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { error = "invalid productId" }); return; }
            SetUnlimitedBody(ctx);
            var buf = await ReadBodyBytes(ctx);
            var r = Files.SaveBuffer(buf, prod.id, ctx.Request.ContentType ?? "");
            if (!r.ok) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { error = r.err }); return; }
            await SendJson(ctx, 200, new { src = r.src });
            return;
        }

        // SPU 列表
        if (path == "/api/spus" && method == "GET") { await SendJson(ctx, 200, Db.Model.spus); return; }

        // SPU 整组导出
        var mSpuExp = RegexMatch(path, @"^/api/spus/([\w]+)/export$");
        if (mSpuExp != null && method == "GET")
        {
            var spu = Db.Model.spus.FirstOrDefault(s => s.id == mSpuExp[1]);
            if (spu == null) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsJsonAsync(new { error = "not found" }); return; }
            var (zip, fname) = Exporter.BuildSpuZip(spu);
            await WriteZip(ctx, zip, fname);
            return;
        }

        // SPU 记一次 POA
        var mSpuPoa = RegexMatch(path, @"^/api/spus/([\w]+)/poa$");
        if (mSpuPoa != null && method == "POST")
        {
            var spu = Db.Model.spus.FirstOrDefault(s => s.id == mSpuPoa[1]);
            if (spu == null) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsJsonAsync(new { error = "not found" }); return; }
            var b = await ReadJson(ctx);
            var now = DateTime.UtcNow.ToString("o");
            spu.poaCount = (spu.poaCount) + 1;
            spu.lastPoaAt = now;
            spu.poaLogs ??= new List<PoaLog>();
            var evidence = new List<EvidenceItem>();
            if (b["evidence"] is JsonArray arr)
            {
                foreach (var e in arr)
                {
                    if (e == null) continue;
                    var esrc = JStr(e, "src");
                    if (string.IsNullOrEmpty(esrc)) continue;
                    var ev = new EvidenceItem
                    {
                        type = JStr(e, "type"),
                        src = esrc,
                        srcs = e["srcs"] is JsonArray sa ? sa.Select(x => x?.GetValue<string>() ?? "").ToList() : null,
                        productId = string.IsNullOrEmpty(JStr(e, "productId")) ? null : JStr(e, "productId"),
                        sku = JStr(e, "sku"),
                        productName = JStr(e, "productName"),
                        section = JStr(e, "section"),
                        kind = JStr(e, "kind"),
                        note = JStr(e, "note")
                    };
                    evidence.Add(ev);
                }
            }
            spu.poaLogs.Insert(0, new PoaLog { at = now, by = user ?? "匿名", scope = JStr(b, "scope").Trim(), note = JStr(b, "note").Trim(), evidence = evidence });
            Db.Save();
            await SendJson(ctx, 200, new { ok = true, poaCount = spu.poaCount, lastPoaAt = spu.lastPoaAt });
            return;
        }

        // 版块
        if (path == "/api/boards" && method == "GET") { await SendJson(ctx, 200, Db.Model.boards); return; }
        if (path == "/api/boards/auto-by-category" && method == "POST")
        {
            var cats = Db.Model.products.Select(p => (p.cat1 ?? "").Trim()).Where(c => !string.IsNullOrEmpty(c)).Distinct().ToList();
            var now = DateTime.UtcNow.ToString("o");
            int created = 0, assigned = 0;
            foreach (var cat in cats)
            {
                var b = Db.Model.boards.FirstOrDefault(x => x.name == cat);
                if (b == null) { b = new Board { id = Db.NewId(), name = cat, note = "按一级分类自动生成", createdAt = now, updatedAt = now }; Db.Model.boards.Insert(0, b); created++; }
                foreach (var p in Db.Model.products) if ((p.cat1 ?? "").Trim() == cat) { p.boardId = b.id; assigned++; }
            }
            Db.Save();
            await SendJson(ctx, 200, new { created, boards = Db.Model.boards.Count, assigned });
            return;
        }
        if (path == "/api/boards" && method == "POST")
        {
            var b = await ReadJson(ctx);
            var name = JStr(b, "name").Trim();
            if (string.IsNullOrEmpty(name)) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { error = "name 必填" }); return; }
            var now = DateTime.UtcNow.ToString("o");
            var board = new Board { id = Db.NewId(), name = name, note = JStr(b, "note").Trim(), createdAt = now, updatedAt = now };
            Db.Model.boards.Insert(0, board); Db.Save();
            ctx.Response.StatusCode = 201;
            await SendJson(ctx, 201, board);
            return;
        }
        var mBoard = RegexMatch(path, @"^/api/boards/([\w]+)$");
        if (mBoard != null)
        {
            var board = Db.Model.boards.FirstOrDefault(x => x.id == mBoard[1]);
            if (board == null) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsJsonAsync(new { error = "not found" }); return; }
            if (method == "PUT")
            {
                var b = await ReadJson(ctx);
                if (b["name"] != null) board.name = JStr(b, "name").Trim();
                if (b["note"] != null) board.note = JStr(b, "note").Trim();
                board.updatedAt = DateTime.UtcNow.ToString("o"); Db.Save();
                await SendJson(ctx, 200, board); return;
            }
            if (method == "DELETE")
            {
                Db.Model.boards = Db.Model.boards.Where(x => x.id != mBoard[1]).ToList();
                foreach (var p in Db.Model.products) if (p.boardId == mBoard[1]) p.boardId = null;
                Db.Save();
                await SendJson(ctx, 200, new { ok = true }); return;
            }
        }

        // SPU 说明书
        var mSpuManual = RegexMatch(path, @"^/api/spus/([\w]+)/manuals$");
        if (mSpuManual != null && method == "POST")
        {
            var spu = Db.Model.spus.FirstOrDefault(s => s.id == mSpuManual[1]);
            if (spu == null) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsJsonAsync(new { error = "not found" }); return; }
            var b = await ReadJson(ctx);
            var kind = JStr(b, "kind") == "accessory" ? "accessory" : "install";
            var r = Files.SaveBase64(JStr(b, "dataUrl"), spu.id);
            if (!r.ok) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { error = r.err }); return; }
            spu.manuals ??= new List<Manual>();
            var old = spu.manuals.FirstOrDefault(m => m.kind == kind);
            var replaced = old != null;
            if (old != null) { Files.DeleteImage(old.src); spu.manuals = spu.manuals.Where(m => m != old).ToList(); }
            var manual = new Manual { id = Db.NewId(), src = r.src, note = JStr(b, "note").Trim(), kind = kind };
            spu.manuals.Add(manual);
            spu.updatedAt = DateTime.UtcNow.ToString("o"); Db.Save();
            var preview = Files.EnsurePdfPreview(manual.src);
            manual.preview = preview;
            await SendJson(ctx, 201, new { id = manual.id, src = manual.src, note = manual.note, kind = manual.kind, preview, replaced });
            return;
        }
        var mSpuManualRaw = RegexMatch(path, @"^/api/spus/([\w]+)/manuals/raw$");
        if (mSpuManualRaw != null && method == "POST")
        {
            var spu = Db.Model.spus.FirstOrDefault(s => s.id == mSpuManualRaw[1]);
            if (spu == null) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsJsonAsync(new { error = "not found" }); return; }
            var kind = q["kind"].ToString() == "accessory" ? "accessory" : "install";
            var note = q["note"].ToString().Trim();
            SetUnlimitedBody(ctx);
            var buf = await ReadBodyBytes(ctx);
            var r = Files.SaveBuffer(buf, spu.id, ctx.Request.ContentType ?? "");
            if (!r.ok) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsJsonAsync(new { error = r.err }); return; }
            spu.manuals ??= new List<Manual>();
            var old = spu.manuals.FirstOrDefault(m => m.kind == kind);
            var replaced = old != null;
            if (old != null) { Files.DeleteImage(old.src); spu.manuals = spu.manuals.Where(m => m != old).ToList(); }
            var manual = new Manual { id = Db.NewId(), src = r.src, note = note, kind = kind };
            spu.manuals.Add(manual);
            spu.updatedAt = DateTime.UtcNow.ToString("o"); Db.Save();
            var preview = Files.EnsurePdfPreview(manual.src);
            manual.preview = preview;
            await SendJson(ctx, 201, new { id = manual.id, src = manual.src, note, kind, preview, replaced });
            return;
        }
        var mSpuManualDel = RegexMatch(path, @"^/api/spus/([\w]+)/manuals/([\w]+)$");
        if (mSpuManualDel != null && method == "DELETE")
        {
            var spu = Db.Model.spus.FirstOrDefault(s => s.id == mSpuManualDel[1]);
            if (spu == null) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsJsonAsync(new { error = "not found" }); return; }
            var mid = mSpuManualDel[2];
            var m = (spu.manuals ?? new List<Manual>()).FirstOrDefault(x => x.id == mid);
            if (m != null) Files.DeleteImage(m.src);
            spu.manuals = (spu.manuals ?? new List<Manual>()).Where(x => x.id != mid).ToList();
            Db.Save();
            await SendJson(ctx, 200, new { ok = true });
            return;
        }

        // 说明书预览
        var mManualPreview = RegexMatch(path, @"^/api/manuals/([\w]+)/preview$");
        if (mManualPreview != null && method == "GET")
        {
            var mid = mManualPreview[1];
            Manual? manual = null;
            foreach (var spu in Db.Model.spus) { manual = (spu.manuals ?? new List<Manual>()).FirstOrDefault(m => m.id == mid); if (manual != null) break; }
            if (manual == null) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsJsonAsync(new { error = "not found" }); return; }
            var preview = Files.EnsurePdfPreview(manual.src);
            if (string.IsNullOrEmpty(preview)) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsJsonAsync(new { error = "preview not available" }); return; }
            var fp = Files.ResolveUnderUploads(preview);
            if (fp == null || !File.Exists(fp)) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsJsonAsync(new { error = "preview not found" }); return; }
            ctx.Response.Headers["Content-Type"] = "image/png";
            await ctx.Response.SendFileAsync(fp);
            return;
        }

        // 单产品导出
        var me2 = RegexMatch(path, @"^/api/export/([\w]+)$");
        if (me2 != null && method == "GET")
        {
            var p = Db.Model.products.FirstOrDefault(x => x.id == me2[1]);
            if (p == null) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsJsonAsync(new { error = "not found" }); return; }
            var section = q["section"].ToString() ?? "all";
            var (zip, fname) = Exporter.BuildProductZip(p, section);
            await WriteZip(ctx, zip, fname);
            return;
        }

        // 产品列表 / 创建
        if (path == "/api/products" && method == "GET")
        {
            var list = Db.Model.products.Select(p =>
            {
                var spu = string.IsNullOrEmpty(p.spuId) ? null : Db.Model.spus.FirstOrDefault(s => s.id == p.spuId);
                var board = string.IsNullOrEmpty(p.boardId) ? null : Db.Model.boards.FirstOrDefault(b => b.id == p.boardId);
                var pd = p.dims?.product;
                var dimSummary = (pd != null && (!string.IsNullOrEmpty(pd.l) || !string.IsNullOrEmpty(pd.w) || !string.IsNullOrEmpty(pd.h) || !string.IsNullOrEmpty(pd.weight)))
                    ? $"{pd.l ?? "-"}×{pd.w ?? "-"}×{pd.h ?? "-"} in · {(pd.weight != null ? pd.weight + " lb" : "—")}" : "";
                var spuMedia = spu?.manuals?.Count ?? 0;
                return new
                {
                    p.id, p.sku, p.productName, p.category, p.poaReason, p.status,
                    p.cat1, p.cat2, p.cat3,
                    spuId = p.spuId, spuName = spu?.name ?? "", spuCode = spu?.code ?? "",
                    boardId = p.boardId, boardName = board?.name ?? "",
                    mediaCount = p.packaging.Count * 2 + p.dims.photos.Count + p.warehouse.Count + spuMedia,
                    dimSummary, p.updatedAt
                };
            }).ToList();
            await SendJson(ctx, 200, list);
            return;
        }
        if (path == "/api/products" && method == "POST")
        {
            var b = await ReadJson(ctx);
            var now = DateTime.UtcNow.ToString("o");
            var prod = new Product
            {
                id = Db.NewId(),
                sku = JStr(b, "sku").Trim(),
                productName = JStr(b, "productName").Trim(),
                spuId = string.IsNullOrEmpty(JStr(b, "spuId")) ? null : JStr(b, "spuId"),
                boardId = string.IsNullOrEmpty(JStr(b, "boardId")) ? null : JStr(b, "boardId"),
                cat1 = JStr(b, "cat1").Trim(), cat2 = JStr(b, "cat2").Trim(), cat3 = JStr(b, "cat3").Trim(),
                category = (!string.IsNullOrEmpty(JStr(b, "category")) ? JStr(b, "category").Trim() : string.Join(" / ", new[] { JStr(b, "cat1"), JStr(b, "cat2"), JStr(b, "cat3") }.Select(x => x.Trim()).Where(x => !string.IsNullOrEmpty(x)))),
                poaReason = JStr(b, "poaReason").Trim(),
                status = string.IsNullOrEmpty(JStr(b, "status")) ? "待调查" : JStr(b, "status"),
                createdAt = now, updatedAt = now
            };
            Db.Model.products.Insert(0, prod); Db.Save();
            ctx.Response.StatusCode = 201;
            await SendJson(ctx, 201, prod);
            return;
        }

        // 单产品
        var m1 = RegexMatch(path, @"^/api/products/([\w]+)$");
        if (m1 != null)
        {
            var prod = Db.Model.products.FirstOrDefault(p => p.id == m1[1]);
            if (prod == null) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsJsonAsync(new { error = "not found" }); return; }
            if (method == "GET") { await SendJson(ctx, 200, prod); return; }
            if (method == "PUT")
            {
                var b = await ReadJson(ctx);
                ApplyProductPatch(prod, b);
                prod.updatedAt = DateTime.UtcNow.ToString("o"); Db.Save();
                await SendJson(ctx, 200, prod); return;
            }
            if (method == "DELETE")
            {
                var dir = Path.Combine(Db.UploadsDir, m1[1]);
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
                Db.Model.products = Db.Model.products.Where(p => p.id != m1[1]).ToList();
                Db.Save();
                await SendJson(ctx, 200, new { ok = true }); return;
            }
        }

        ctx.Response.StatusCode = 404;
        await ctx.Response.WriteAsJsonAsync(new { error = "not found" });
    }

    // ============ 辅助 ============
    private static void ApplyProductPatch(Product prod, JsonNode b)
    {
        if (b["sku"] != null) prod.sku = JStr(b, "sku").Trim();
        if (b["productName"] != null) prod.productName = JStr(b, "productName").Trim();
        if (b["spuId"] != null) prod.spuId = string.IsNullOrEmpty(JStr(b, "spuId")) ? null : JStr(b, "spuId");
        if (b["boardId"] != null) prod.boardId = string.IsNullOrEmpty(JStr(b, "boardId")) ? null : JStr(b, "boardId");
        if (b["category"] != null) prod.category = JStr(b, "category").Trim();
        if (b["cat1"] != null) prod.cat1 = JStr(b, "cat1").Trim();
        if (b["cat2"] != null) prod.cat2 = JStr(b, "cat2").Trim();
        if (b["cat3"] != null) prod.cat3 = JStr(b, "cat3").Trim();
        if (b["poaReason"] != null) prod.poaReason = JStr(b, "poaReason").Trim();
        if (b["status"] != null) prod.status = JStr(b, "status").Trim();
        if (b["packaging"] != null) prod.packaging = b["packaging"]!.Deserialize<List<PackagingEntry>>(JsonOpts) ?? new();
        if (b["install"] != null) prod.install = b["install"]!.Deserialize<List<object>>(JsonOpts) ?? new();
        if (b["accessory"] != null) prod.accessory = b["accessory"]!.Deserialize<List<object>>(JsonOpts) ?? new();
        if (b["dims"] != null) prod.dims = b["dims"]!.Deserialize<Dims>(JsonOpts) ?? new();
        if (b["warehouse"] != null) prod.warehouse = b["warehouse"]!.Deserialize<List<WarehouseEntry>>(JsonOpts) ?? new();
    }

    private static async Task<JsonNode?> ReadJson(HttpContext ctx)
    {
        var txt = await ReadBodyString(ctx);
        if (string.IsNullOrWhiteSpace(txt)) return new JsonObject();
        return JsonNode.Parse(txt);
    }

    private static async Task<string> ReadBodyString(HttpContext ctx)
    {
        using var r = new StreamReader(ctx.Request.Body);
        return await r.ReadToEndAsync();
    }

    private static async Task<byte[]> ReadBodyBytes(HttpContext ctx)
    {
        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms);
        return ms.ToArray();
    }

    private static void SetUnlimitedBody(HttpContext ctx)
    {
        var feat = ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (feat != null) feat.MaxRequestBodySize = null;
    }

    private static string JStr(JsonNode? n, string key)
    {
        var v = n?[key];
        if (v == null || v.GetValueKind() == JsonValueKind.Null) return "";
        return v.GetValue<string>();
    }

    private static string DecodeBase64String(string s)
    {
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(s)); }
        catch { return s; }
    }

    private static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        text = text.Replace("\r\n", "\n").Replace("\r", "\n");
        int i = 0;
        while (i < text.Length)
        {
            var row = new List<string>();
            var field = new StringBuilder();
            bool inQ = false;
            while (i < text.Length)
            {
                var c = text[i];
                if (inQ)
                {
                    if (c == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i += 2; continue; } inQ = false; i++; continue; }
                    field.Append(c); i++; continue;
                }
                if (c == '"') { inQ = true; i++; continue; }
                if (c == ',') { row.Add(field.ToString()); field.Clear(); i++; continue; }
                if (c == '\n') { row.Add(field.ToString()); rows.Add(row); field.Clear(); i++; break; }
                field.Append(c); i++;
            }
            if (row.Count == 0 && field.Length == 0) break;
            if (i >= text.Length && (row.Count > 0 || field.Length > 0)) { row.Add(field.ToString()); rows.Add(row); break; }
        }
        return rows;
    }

    private static object RunImport(List<List<string>> rows)
    {
        var header = rows[0].Select(h => (h ?? "").Trim()).ToList();
        int H(string k) { var i = header.IndexOf(k); if (i >= 0) return i; return header.FindIndex(h => h != null && h.Contains(k)); }
        int FindCol(params string[] cands) { foreach (var c in cands) { var i = header.FindIndex(h => h != null && h.Contains(c)); if (i >= 0) return i; } return -1; }
        int iSku = H("SKU"), iName = H("产品名称");
        int iBoard = H("版块") >= 0 ? H("版块") : H("版块名称");
        int iCat1 = H("一级分类") >= 0 ? H("一级分类") : H("类目");
        int iCat2 = H("二级分类"), iCat3 = H("三级分类");
        int iSeries = H("产品系列编码") >= 0 ? H("产品系列编码") : H("产品组SPU编号");
        int iSpName = H("产品组SPU名称"), iReason = H("调查原因"), iStatus = H("状态");
        int ipL = FindCol("产品长(in)", "长英寸", "长（inch", "长(inch", "长（in"), ipW = FindCol("产品宽(in)", "宽英寸", "宽（inch", "宽(inch", "宽（in"), ipH = FindCol("产品高(in)", "高英寸", "高（inch", "高(inch", "高（in");
        int ipWt = FindCol("产品重(lb)", "净重磅（lb）", "净重磅", "净重量(lb)", "净重量");
        int ioL = FindCol("外包装长(in)"), ioW = FindCol("外包装宽(in)"), ioH = FindCol("外包装高(in)");
        int ioWt = FindCol("外包装重(lb)", "毛重磅（lb）", "毛重磅", "毛重量(lb)", "毛重量");
        var now = DateTime.UtcNow.ToString("o");
        var created = new List<string>(); var skipped = new List<string>();
        var existing = new HashSet<string>(Db.Model.products.Select(p => (p.sku ?? "").Trim()).Where(x => !string.IsNullOrEmpty(x)));
        for (int r = 1; r < rows.Count; r++)
        {
            var row = rows[r];
            var sku = (row.Count > iSku ? (row[iSku] ?? "").Trim() : "");
            if (string.IsNullOrEmpty(sku)) continue;
            if (existing.Contains(sku)) { skipped.Add(sku); continue; }
            string? boardId = null;
            var boardName = (iBoard >= 0 && row.Count > iBoard) ? (row[iBoard] ?? "").Trim() : "";
            if (!string.IsNullOrEmpty(boardName))
            {
                var b = Db.Model.boards.FirstOrDefault(x => x.name.Trim().Equals(boardName, StringComparison.OrdinalIgnoreCase));
                if (b == null) { b = new Board { id = Db.NewId(), name = boardName, note = "", createdAt = now, updatedAt = now }; Db.Model.boards.Insert(0, b); }
                boardId = b.id;
            }
            string? spuId = null;
            var series = (iSeries >= 0 && row.Count > iSeries) ? (row[iSeries] ?? "").Trim() : "";
            if (!string.IsNullOrEmpty(series))
            {
                var spu = Db.Model.spus.FirstOrDefault(s => s.code == series);
                if (spu == null) { spu = new Spu { id = Db.NewId(), code = series, name = (iSpName >= 0 && row.Count > iSpName ? (row[iSpName] ?? "").Trim() : "") ?? series, note = "", manuals = new(), createdAt = now, updatedAt = now }; Db.Model.spus.Insert(0, spu); }
                spuId = spu.id;
            }
            string T(int idx) => (idx >= 0 && row.Count > idx) ? (row[idx] ?? "").Trim() : "";
            string cat1 = T(iCat1); string cat2 = T(iCat2); string cat3 = T(iCat3);
            var category = string.Join(" / ", new[] { cat1, cat2, cat3 }.Where(x => !string.IsNullOrEmpty(x)));
            var dims = new Dims
            {
                unit = "in",
                product = new DimBox { l = T(ipL), w = T(ipW), h = T(ipH), weight = T(ipWt) },
                outer = new DimBox { l = T(ioL), w = T(ioW), h = T(ioH), weight = T(ioWt) },
                photos = new()
            };
            Db.Model.products.Insert(0, new Product
            {
                id = Db.NewId(), sku = sku, productName = (iName >= 0 && row.Count > iName ? (row[iName] ?? "").Trim() : "") ?? sku,
                spuId = spuId, boardId = boardId, cat1 = cat1, cat2 = cat2, cat3 = cat3, category = category,
                poaReason = (iReason >= 0 && row.Count > iReason ? (row[iReason] ?? "").Trim() : ""),
                status = (iStatus >= 0 && row.Count > iStatus ? (row[iStatus] ?? "").Trim() : "") ?? "待调查",
                packaging = new(), install = new(), accessory = new(), dims = dims, warehouse = new(),
                createdAt = now, updatedAt = now
            });
            created.Add(sku);
        }
        Db.Save();
        return new { created = created.Count, skipped = skipped.Count, skus = created, errors = new object[] { } };
    }

    private static async Task<List<List<string>>?> ParseXlsx(byte[] buf)
    {
        var py = FindPython();
        if (py == null) throw new Exception("xlsx 导入需要 Python 环境（未找到 python）");
        var tmp = Path.Combine(Path.GetTempPath(), "poa_xlsx_" + Db.NewId() + ".xlsx");
        await File.WriteAllBytesAsync(tmp, buf);
        var psi = new System.Diagnostics.ProcessStartInfo(py, $"\"{Path.Combine(AppContext.BaseDirectory, "parse_xlsx.py")}\" \"{tmp}\"")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using var proc = System.Diagnostics.Process.Start(psi)!;
        var outp = await proc.StandardOutput.ReadToEndAsync();
        var errp = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        try { File.Delete(tmp); } catch { }
        if (proc.ExitCode != 0) throw new Exception(errp);
        return JsonSerializer.Deserialize<List<List<string>>>(outp);
    }

    private static string? FindPython()
    {
        foreach (var c in new[] { "C:/Users/Administrator/.workbuddy/binaries/python/versions/3.13.12/python.exe", "python3", "python" })
            if (File.Exists(c)) return c;
        return null;
    }

    private static async Task SendJson(HttpContext ctx, int code, object obj)
    {
        ctx.Response.StatusCode = code;
        ctx.Response.Headers["Content-Type"] = "application/json; charset=utf-8";
        await ctx.Response.WriteAsJsonAsync(obj, JsonOpts);
    }

    private static async Task WriteZip(HttpContext ctx, byte[] zip, string fileName)
    {
        ctx.Response.Headers["Content-Type"] = "application/zip";
        ctx.Response.Headers["Content-Disposition"] = $"attachment; filename=\"export.zip\"; filename*=UTF-8''{Uri.EscapeDataString(fileName)}";
        await ctx.Response.Body.WriteAsync(zip, 0, zip.Length);
    }

    private static string[]? RegexMatch(string path, string pattern)
    {
        var m = System.Text.RegularExpressions.Regex.Match(path, pattern);
        if (!m.Success) return null;
        var g = new List<string>();
        // Groups[0] 为整个匹配，第一个捕获组从下标 1 开始，与所有调用方 X[1] 语义一致
        for (int i = 0; i < m.Groups.Count; i++) g.Add(m.Groups[i].Value);
        return g.ToArray();
    }
}
