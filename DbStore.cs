using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace PoaNet;

public static class Db
{
    public static DbModel Model { get; private set; } = new();
    public static string DataDir { get; private set; } = "";
    public static string UploadsDir { get; private set; } = "";
    public static string PublicDir { get; private set; } = "";
    public static string DbFile { get; private set; } = "";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static void Init(string dataDir, string uploadsDir, string publicDir)
    {
        DataDir = dataDir;
        UploadsDir = uploadsDir;
        PublicDir = publicDir;
        DbFile = Path.Combine(dataDir, "db.json");
        Directory.CreateDirectory(uploadsDir);
        Load();
    }

    public static void Load()
    {
        if (File.Exists(DbFile))
        {
            try
            {
                var raw = File.ReadAllText(DbFile);
                Model = JsonSerializer.Deserialize<DbModel>(raw, JsonOpts) ?? new DbModel();
            }
            catch
            {
                Model = new DbModel();
            }
        }
        else
        {
            Model = new DbModel();
        }
        Normalize();
    }

    // 对齐 Node loadDB 的兼容/清洗逻辑
    private static void Normalize()
    {
        if (Model.spus == null) Model.spus = new List<Spu>();
        if (Model.boards == null) Model.boards = new List<Board>();
        if (Model.products == null) Model.products = new List<Product>();

        foreach (var s in Model.spus)
        {
            s.manuals ??= new List<Manual>();
            // 每 SPU 每 kind 仅保留一份说明书，删除多余文件
            var kept = new List<Manual>();
            Manual? installKeep = null, accessoryKeep = null;
            foreach (var m in s.manuals)
            {
                if (m.kind == "accessory")
                {
                    if (accessoryKeep == null) accessoryKeep = m; else Files.DeleteImage(m.src);
                }
                else
                {
                    if (installKeep == null) installKeep = m; else Files.DeleteImage(m.src);
                }
            }
            if (installKeep != null) kept.Add(installKeep);
            if (accessoryKeep != null) kept.Add(accessoryKeep);
            s.manuals = kept;

            if (s.poaLogs == null) s.poaLogs = new List<PoaLog>();
        }

        foreach (var p in Model.products)
        {
            p.packaging ??= new List<PackagingEntry>();
            p.accessory ??= new List<object>();
            p.install ??= new List<object>();
            p.dims ??= new Dims();
            p.dims.product ??= new DimBox();
            p.dims.outer ??= new DimBox();
            p.dims.photos ??= new List<DimPhoto>();
            p.warehouse ??= new List<WarehouseEntry>();

            // 旧产品级说明书迁移进所属 SPU（按 src 去重，幂等）
            if (!string.IsNullOrEmpty(p.spuId))
            {
                var spu = Model.spus.FirstOrDefault(x => x.id == p.spuId);
                if (spu != null)
                {
                    var rawInstall = p.install.OfType<JsonElement>().ToList();
                    var rawAccessory = p.accessory.OfType<JsonElement>().ToList();
                    foreach (var e in rawInstall.Concat(rawAccessory))
                    {
                        var kind = rawAccessory.Contains(e) ? "accessory" : "install";
                        var src = e.TryGetProperty("src", out var srcEl) ? srcEl.GetString() : null;
                        if (!string.IsNullOrEmpty(src) && !spu.manuals.Any(x => x.src == src))
                            spu.manuals.Add(new Manual { id = NewId(), src = src, note = "", kind = kind });
                    }
                }
            }
            p.install = new List<object>();
            p.accessory = new List<object>();
        }
    }

    public static void Save()
    {
        Directory.CreateDirectory(DataDir);
        var tmp = DbFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Model, JsonOpts));
        File.Move(tmp, DbFile, true); // 同目录 rename 近似原子，避免写入中途损坏
    }

    public static string NewId()
    {
        var ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString("x");
        var rnd = Guid.NewGuid().ToString("N")[..8];
        return ms + rnd;
    }

    // 启动即留一份备份
    public static void BackupOnce()
    {
        try { if (File.Exists(DbFile)) File.Copy(DbFile, DbFile + ".bak", true); } catch { }
    }
}
