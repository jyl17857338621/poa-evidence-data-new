using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PoaNet;

public static class Exporter
{
    private static readonly Dictionary<string, string> SectionLabel = new()
    {
        ["packaging"] = "内外包装对比图", ["install"] = "安装说明书",
        ["accessory"] = "配件说明书", ["dims"] = "尺寸信息", ["warehouse"] = "工厂/托盘照片"
    };

    private static Spu? GetSpu(Product p) => string.IsNullOrEmpty(p.spuId) ? null : Db.Model.spus.FirstOrDefault(s => s.id == p.spuId);
    private static List<Manual> SpuManualsOf(Product p) => GetSpu(p)?.manuals ?? new List<Manual>();

    private class MediaItem { public string? zip; public string? disk; public string note = ""; }

    private static Dictionary<string, List<MediaItem>> GatherMedia(Product p)
    {
        var sm = SpuManualsOf(p);
        var r = new Dictionary<string, List<MediaItem>>();
        var pkg = new List<MediaItem>();
        foreach (var e in p.packaging.Select((x, i) => new { x, i }))
        {
            var kind = e.x.kind;
            var tag = (kind == "内包装") ? "inner" : "outer";
            if (e.x.before != null) pkg.Add(new MediaItem { zip = $"packaging/{tag}_{e.i + 1}_before.{Files.ExtOf(e.x.before)}", disk = e.x.before, note = e.x.note ?? "" });
            if (e.x.after != null) pkg.Add(new MediaItem { zip = $"packaging/{tag}_{e.i + 1}_after.{Files.ExtOf(e.x.after)}", disk = e.x.after, note = e.x.note ?? "" });
        }
        r["packaging"] = pkg;
        r["install"] = sm.Where(m => m.kind == "install").Select((e, i) => new MediaItem { zip = $"install/install_{i + 1}.{Files.ExtOf(e.src)}", disk = e.src, note = e.note ?? "" }).ToList();
        r["accessory"] = sm.Where(m => m.kind == "accessory").Select((e, i) => new MediaItem { zip = $"accessory/accessory_{i + 1}.{Files.ExtOf(e.src)}", disk = e.src, note = e.note ?? "" }).ToList();
        r["dims"] = p.dims.photos.Select((e, i) => new MediaItem { zip = $"dims/dim_{i + 1}.{Files.ExtOf(e.src)}", disk = e.src, note = e.note ?? "" }).ToList();
        r["warehouse"] = p.warehouse.Select((e, i) => new MediaItem { zip = $"warehouse/warehouse_{i + 1}.{Files.ExtOf(e.src)}", disk = e.src, note = e.note ?? "" }).ToList();
        return r;
    }

    private static string Esc(string? s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    private static string Img(string z) => $"<img src=\"{z}\" style=\"max-width:100%;max-height:420px;border:1px solid #ddd;border-radius:6px;margin:6px 0\">";
    private static bool IsPdf(string z) => Regex.IsMatch(z, "\\.pdf$", RegexOptions.IgnoreCase);
    private static string MediaThumb(string z) => IsPdf(z)
        ? $"<iframe src=\"{z}\" style=\"width:340px;height:420px;border:1px solid #ddd;border-radius:6px\"></iframe>"
        : Img(z);

    private static string BuildReportHtml(Product p, string section)
    {
        var media = GatherMedia(p);
        bool Include(string s) => section == "all" || section == s;
        var body = new StringBuilder();

        if (Include("packaging"))
        {
            body.Append("<h2>① 内外包装对比图</h2>");
            foreach (var e in p.packaging)
            {
                body.Append($"<div style=\"margin:14px 0;padding:12px;border:1px solid #eee;border-radius:8px\"><b>{Esc(e.kind)}</b><div style=\"display:flex;gap:16px;flex-wrap:wrap\">");
                body.Append(e.before != null ? $"<div><div style=\"color:#c0392b;font-size:13px\">改进前</div>{Img(e.before)}</div>" : "<div><div style=\"color:#c0392b;font-size:13px\">改进前</div><div style=\"color:#bbb\">未上传</div></div>");
                body.Append(e.after != null ? $"<div><div style=\"color:#1e8e5a;font-size:13px\">改进后</div>{Img(e.after)}</div>" : "<div><div style=\"color:#1e8e5a;font-size:13px\">改进后</div><div style=\"color:#bbb\">未上传</div></div>");
                body.Append("</div>");
                if (!string.IsNullOrEmpty(e.note)) body.Append($"<div style=\"color:#555;font-size:13px\">说明:{Esc(e.note)}</div>");
                body.Append("</div>");
            }
        }

        string Gallery(string title, List<MediaItem> arr, string sec)
        {
            if (!Include(sec) || arr.Count == 0) return "";
            var h = $"<h2>{title}</h2><div style=\"display:flex;gap:16px;flex-wrap:wrap\">";
            foreach (var e in arr)
                h += $"<div style=\"max-width:360px\"><div>{MediaThumb(e.zip!)}</div>{(!string.IsNullOrEmpty(e.note) ? $"<div style=\"color:#555;font-size:13px\">{Esc(e.note)}</div>" : "")}</div>";
            return h + "</div>";
        }
        body.Append(Gallery("② 安装说明书", media["install"], "install"));
        body.Append(Gallery("③ 配件说明书", media["accessory"], "accessory"));

        if (Include("dims"))
        {
            var d = p.dims;
            body.Append("<h2>④ 尺寸信息</h2><table border=\"1\" cellspacing=\"0\" cellpadding=\"6\" style=\"border-collapse:collapse;font-size:13px\">");
            body.Append("<tr><th>项目</th><th>长 (in)</th><th>宽 (in)</th><th>高 (in)</th><th>重量 (lb)</th></tr>");
            body.Append($"<tr><td>产品尺寸</td><td>{Esc(d.product?.l)}</td><td>{Esc(d.product?.w)}</td><td>{Esc(d.product?.h)}</td><td>{Esc(d.product?.weight)}</td></tr>");
            body.Append($"<tr><td>外包装尺寸</td><td>{Esc(d.outer?.l)}</td><td>{Esc(d.outer?.w)}</td><td>{Esc(d.outer?.h)}</td><td>{Esc(d.outer?.weight)}</td></tr>");
            body.Append("</table>");
            if (media["dims"].Count > 0)
            {
                body.Append("<div style=\"display:flex;gap:16px;flex-wrap:wrap;margin-top:10px\">");
                foreach (var e in media["dims"])
                    body.Append($"<div><div>{Img(e.zip!)}</div>{(!string.IsNullOrEmpty(e.note) ? $"<div style=\"color:#555;font-size:13px\">{Esc(e.note)}</div>" : "")}</div>");
                body.Append("</div>");
            }
        }
        body.Append(Gallery("⑤ 工厂/托盘照片", media["warehouse"], "warehouse"));

        var meta = $"<h1>{Esc(p.productName)} <span style=\"font-size:14px;color:#666\">({Esc(p.sku)})</span></h1>" +
                   $"<p style=\"color:#666;font-size:13px\">类目:{Esc(p.category) ?? "—"} ｜ 状态:{Esc(p.status)} ｜ 调查原因:{Esc(p.poaReason) ?? "—"} ｜ 导出时间:{DateTime.Now:yyyy-MM-dd HH:mm:ss}</p>";
        return $"<!DOCTYPE html><html lang=\"zh-CN\"><head><meta charset=\"UTF-8\"><title>POA 证据 - {Esc(p.sku)}</title></head><body style=\"font-family:-apple-system,'Microsoft YaHei',sans-serif;max-width:1000px;margin:20px auto;padding:0 16px;color:#1b2733\">{meta}{body}</body></html>";
    }

    private static byte[]? ReadDisk(string? rel)
    {
        if (string.IsNullOrEmpty(rel)) return null;
        var fp = Files.ResolveUnderUploads(rel);
        return (fp != null && File.Exists(fp)) ? File.ReadAllBytes(fp) : null;
    }

    private static byte[] MakeZip(List<(string name, byte[] data)> files)
    {
        using var ms = new MemoryStream();
        using (var za = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            foreach (var f in files)
            {
                var entry = za.CreateEntry(f.name.Replace('\\', '/'));
                using var es = entry.Open();
                es.Write(f.data, 0, f.data.Length);
            }
        }
        return ms.ToArray();
    }

    private static readonly System.Text.Json.JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static string JsonString(object o) => System.Text.Json.JsonSerializer.Serialize(o, JsonOpts);

    public static (byte[] zip, string fileName) BuildProductZip(Product p, string section)
    {
        bool Want(string s) => section == "all" || section == s;
        var files = new List<(string, byte[])>();
        var media = GatherMedia(p);
        void Push(MediaItem? x) { if (x?.disk == null) return; var b = ReadDisk(x.disk); if (b != null) files.Add((x.zip!, b)); }
        if (Want("packaging")) foreach (var e in media["packaging"]) Push(e);
        if (Want("install")) foreach (var e in media["install"]) Push(e);
        if (Want("accessory")) foreach (var e in media["accessory"]) Push(e);
        if (Want("dims")) foreach (var e in media["dims"]) Push(e);
        if (Want("warehouse")) foreach (var e in media["warehouse"]) Push(e);
        files.Add(("index.html", Encoding.UTF8.GetBytes(BuildReportHtml(p, section))));
        files.Add(("data.json", Encoding.UTF8.GetBytes(JsonString(p))));
        var buf = MakeZip(files);
        var fname = $"{(string.IsNullOrEmpty(p.sku) ? "product" : p.sku)}_{(section == "all" ? "全部资料" : (SectionLabel.TryGetValue(section, out var l) ? l : section))}.zip";
        return (buf, fname);
    }

    public static (byte[] zip, string fileName) BuildSpuZip(Spu spu)
    {
        var members = Db.Model.products.Where(p => p.spuId == spu.id).ToList();
        var files = new List<(string, byte[])>();
        foreach (var p in members)
        {
            var prefix = $"SKU_{Regex.Replace(p.sku ?? "product", "[^\\w-]", "_")}/";
            var media = GatherMedia(p);
            void Push(MediaItem? x) { if (x?.disk == null) return; var b = ReadDisk(x.disk); if (b != null) files.Add((prefix + x.zip!, b)); }
            foreach (var e in media["packaging"]) Push(e);
            foreach (var e in media["install"]) Push(e);
            foreach (var e in media["accessory"]) Push(e);
            foreach (var e in media["dims"]) Push(e);
            foreach (var e in media["warehouse"]) Push(e);
            files.Add((prefix + "index.html", Encoding.UTF8.GetBytes(BuildReportHtml(p, "all"))));
            files.Add((prefix + "data.json", Encoding.UTF8.GetBytes(JsonString(p))));
        }
        var links = string.Join("", members.Select(p =>
            $"<li><a href=\"SKU_{Regex.Replace(p.sku ?? "product", "[^\\w-]", "_")}/index.html\">{Esc(p.sku)} {Esc(p.productName)}</a>（{p.packaging.Count * 2 + p.dims.photos.Count + p.warehouse.Count} 张素材）</li>"));
        var root = $"<!DOCTYPE html><html lang=\"zh-CN\"><head><meta charset=\"UTF-8\"><title>SPU 资料包 - {Esc(spu.code)}</title></head><body style=\"font-family:-apple-system,'Microsoft YaHei',sans-serif;max-width:900px;margin:20px auto;padding:0 16px;color:#1b2733\"><h1>SPU 资料包</h1><p style=\"color:#666\">组编号: <b>{Esc(spu.code)}</b> ｜ 组名: <b>{Esc(spu.name)}</b> ｜ 含 {members.Count} 个 SKU ｜ 导出时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}</p>{(!string.IsNullOrEmpty(spu.note) ? $"<p style=\"color:#555\">备注:{Esc(spu.note)}</p>" : "")}<h2>SKU 清单</h2><ul>{links}</ul></body></html>";
        files.Add(("index.html", Encoding.UTF8.GetBytes(root)));
        files.Add(("spu.json", Encoding.UTF8.GetBytes(JsonString(spu))));
        var buf = MakeZip(files);
        var fname = $"SPU_{Esc(spu.code) ?? "group"}_全部资料({members.Count}个SKU).zip";
        return (buf, fname);
    }
}
