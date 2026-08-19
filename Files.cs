using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace PoaNet;

public static class Files
{
    private static readonly Dictionary<string, string> Mime = new()
    {
        [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png",
        [".gif"] = "image/gif", [".webp"] = "image/webp", [".svg"] = "image/svg+xml",
        [".bmp"] = "image/bmp", [".pdf"] = "application/pdf"
    };

    private static readonly string[] PyCandidates = new[]
    {
        "C:/Users/Administrator/.workbuddy/binaries/python/versions/3.13.12/python.exe",
        "C:/Users/Administrator/.workbuddy/binaries/python/envs/default/Scripts/python.exe",
        "python3", "python"
    };

    public static string MimeFor(string ext) => Mime.TryGetValue(ext.ToLowerInvariant(), out var m) ? m : "application/octet-stream";

    // 把 "/uploads/owner/file" 解析为 uploads 目录下的绝对路径，并防止路径穿越
    public static string? ResolveUnderUploads(string rel)
    {
        var s = rel.TrimStart('/');
        const string prefix = "uploads/";
        if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) s = s[prefix.Length..];
        var full = Path.GetFullPath(Path.Combine(Db.UploadsDir, s.Replace('/', Path.DirectorySeparatorChar)));
        var uploadsFull = Path.GetFullPath(Db.UploadsDir);
        if (!full.StartsWith(uploadsFull, StringComparison.OrdinalIgnoreCase)) return null;
        return full;
    }

    public static (string mime, string ext)? Sniff(byte[] buf)
    {
        if (buf.Length < 12) return null;
        var head = buf.AsSpan(0, 12);
        if (head[0] == 0x25 && head[1] == 0x50 && head[2] == 0x44 && head[3] == 0x46) return ("application/pdf", "pdf");
        if (head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47) return ("image/png", "png");
        if (head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return ("image/jpeg", "jpg");
        if (head[0] == 0x47 && head[1] == 0x49 && head[2] == 0x46) return ("image/gif", "gif");
        if (head[0] == 0x52 && head[1] == 0x49 && head[2] == 0x46 && head[3] == 0x46 && head[8] == 0x57 && head[9] == 0x45 && head[10] == 0x42 && head[11] == 0x50) return ("image/webp", "webp");
        if (head[0] == 0x42 && head[1] == 0x4D) return ("image/bmp", "bmp");
        var maybeSvg = Encoding.Latin1.GetString(buf, 0, Math.Min(200, buf.Length));
        if (System.Text.RegularExpressions.Regex.IsMatch(maybeSvg, "<svg[\\s>]", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return ("image/svg+xml", "svg");
        return null;
    }

    public static (bool ok, string src, string err) SaveBuffer(byte[] buf, string ownerId, string? declaredMime)
    {
        if (buf == null || buf.Length == 0) return (false, "", "文件数据为空");
        var info = Sniff(buf);
        var dm = (declaredMime ?? "").ToLowerInvariant();
        if (!string.IsNullOrEmpty(dm) && dm != "application/octet-stream")
        {
            if (dm.StartsWith("image/"))
            {
                var ext = dm.Split('/')[1];
                if (ext == "jpeg") ext = "jpg";
                if (!new[] { "jpg", "png", "gif", "webp", "svg", "bmp" }.Contains(ext)) ext = "img";
                info = (dm, ext);
            }
            else if (dm == "application/pdf") info = ("application/pdf", "pdf");
        }
        if (info == null) return (false, "", "仅支持 PDF 和图片(JPG/PNG/GIF/WebP/SVG/BMP)");
        var dir = Path.Combine(Db.UploadsDir, ownerId);
        Directory.CreateDirectory(dir);
        var fname = Db.NewId() + "." + info.Value.ext;
        File.WriteAllBytes(Path.Combine(dir, fname), buf);
        return (true, "/uploads/" + ownerId + "/" + fname, "");
    }

    public static (bool ok, string src, string err) SaveBase64(string? dataUrl, string ownerId)
    {
        if (string.IsNullOrEmpty(dataUrl) || !dataUrl.StartsWith("data:"))
            return (false, "", "文件数据为空或不是 data: 格式");
        var m = System.Text.RegularExpressions.Regex.Match(dataUrl, "^data:([\\w/\\-\\.]+);base64,(.+)$", System.Text.RegularExpressions.RegexOptions.Singleline);
        if (!m.Success) return (false, "", "文件不是合法的 base64 编码");
        try
        {
            var buf = Convert.FromBase64String(m.Groups[2].Value);
            return SaveBuffer(buf, ownerId, m.Groups[1].Value.ToLowerInvariant());
        }
        catch (Exception e) { return (false, "", "base64 解码失败: " + e.Message); }
    }

    public static void DeleteImage(string? rel)
    {
        if (string.IsNullOrEmpty(rel)) return;
        var p = ResolveUnderUploads(rel);
        if (p != null) { try { if (File.Exists(p)) File.Delete(p); } catch { } }
        var preview = PreviewPathOf(rel);
        if (preview != null)
        {
            var pp = ResolveUnderUploads(preview);
            if (pp != null) { try { if (File.Exists(pp)) File.Delete(pp); } catch { } }
        }
    }

    public static string? PreviewPathOf(string? src)
    {
        if (string.IsNullOrEmpty(src) || !System.Text.RegularExpressions.Regex.IsMatch(src, "\\.pdf$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return null;
        return System.Text.RegularExpressions.Regex.Replace(src, "\\.pdf$", ".pdf.preview.png", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private static string? FindPython()
    {
        foreach (var c in PyCandidates)
        {
            try { if (File.Exists(c)) return c; } catch { }
        }
        return null;
    }

    public static string? EnsurePdfPreview(string src)
    {
        var preview = PreviewPathOf(src);
        if (preview == null) return null;
        var pdf = ResolveUnderUploads(src);
        var png = ResolveUnderUploads(preview);
        if (pdf == null || png == null) return null;
        if (File.Exists(png)) return preview;
        var py = FindPython();
        if (py == null) return null;
        try
        {
            var psi = new ProcessStartInfo(py, $"\"{Path.Combine(AppContext.BaseDirectory, "pdf_preview.py")}\" \"{pdf}\" \"{png}\" 1200")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(30000);
            if (proc != null && proc.ExitCode == 0 && File.Exists(png)) return preview;
        }
        catch { }
        return null;
    }

    public static string ExtOf(string? src)
    {
        var m = (src ?? "").Split('.');
        var e = m.Length > 1 ? m[^1] : "";
        return System.Text.RegularExpressions.Regex.IsMatch(e, "^[a-z0-9]+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? e.ToLowerInvariant() : "jpg";
    }
}
