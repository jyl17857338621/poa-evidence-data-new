using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PoaNet;

public class DbModel
{
    public List<Product> products { get; set; } = new();
    public List<Spu> spus { get; set; } = new();
    public List<Board> boards { get; set; } = new();
}

public class Product
{
    public string id { get; set; } = "";
    public string sku { get; set; } = "";
    public string productName { get; set; } = "";
    public string? spuId { get; set; }
    public string? boardId { get; set; }
    public string cat1 { get; set; } = "";
    public string cat2 { get; set; } = "";
    public string cat3 { get; set; } = "";
    public string category { get; set; } = "";
    public string poaReason { get; set; } = "";
    public string status { get; set; } = "待调查";
    public List<PackagingEntry> packaging { get; set; } = new();
    public List<object> install { get; set; } = new();   // 说明书已统一到 SPU 层，保留为空数组以兼容
    public List<object> accessory { get; set; } = new();
    public Dims dims { get; set; } = new();
    public List<WarehouseEntry> warehouse { get; set; } = new();
    public string createdAt { get; set; } = "";
    public string updatedAt { get; set; } = "";
}

public class PackagingEntry
{
    public string id { get; set; } = "";
    public string kind { get; set; } = "";      // 内包装 / 外包装
    public string? before { get; set; }
    public string? after { get; set; }
    public string note { get; set; } = "";
}

public class WarehouseEntry
{
    public string id { get; set; } = "";
    public string? src { get; set; }
    public string note { get; set; } = "";
}

public class Dims
{
    public string unit { get; set; } = "in";
    public DimBox product { get; set; } = new();
    public DimBox outer { get; set; } = new();
    public List<DimPhoto> photos { get; set; } = new();
}

public class DimBox
{
    public string l { get; set; } = "";
    public string w { get; set; } = "";
    public string h { get; set; } = "";
    public string weight { get; set; } = "";
}

public class DimPhoto
{
    public string id { get; set; } = "";
    public string? src { get; set; }
    public string note { get; set; } = "";
}

public class Spu
{
    public string id { get; set; } = "";
    public string code { get; set; } = "";
    public string name { get; set; } = "";
    public string note { get; set; } = "";
    public List<Manual> manuals { get; set; } = new();
    public int poaCount { get; set; } = 0;
    public string? lastPoaAt { get; set; }
    public List<PoaLog> poaLogs { get; set; } = new();
    public string createdAt { get; set; } = "";
    public string updatedAt { get; set; } = "";
}

public class Manual
{
    public string id { get; set; } = "";
    public string src { get; set; } = "";
    public string note { get; set; } = "";
    public string kind { get; set; } = "install"; // install | accessory
    public string? preview { get; set; }
}

public class PoaLog
{
    public string at { get; set; } = "";
    public string by { get; set; } = "";
    public string scope { get; set; } = "";
    public string note { get; set; } = "";
    public List<EvidenceItem> evidence { get; set; } = new();
}

public class EvidenceItem
{
    public string type { get; set; } = "";     // spu_manual | product_photo | product_report
    public string src { get; set; } = "";
    public List<string>? srcs { get; set; }
    public string? productId { get; set; }
    public string sku { get; set; } = "";
    public string productName { get; set; } = "";
    public string section { get; set; } = "";
    public string kind { get; set; } = "";
    public string note { get; set; } = "";
}

public class Board
{
    public string id { get; set; } = "";
    public string name { get; set; } = "";
    public string note { get; set; } = "";
    public string createdAt { get; set; } = "";
    public string updatedAt { get; set; } = "";
}

public class UserRec
{
    public string username { get; set; } = "";
    public string salt { get; set; } = "";
    public string hash { get; set; } = "";
    public string role { get; set; } = "editor";
    public string createdAt { get; set; } = "";
    public string? updatedAt { get; set; }
}

// 扁平化素材视图条目（/api/photos）
public class PhotoFlat
{
    public string productId { get; set; } = "";
    public string sku { get; set; } = "";
    public string productName { get; set; } = "";
    public string status { get; set; } = "";
    public string section { get; set; } = "";
    public string kind { get; set; } = "";
    public string? src { get; set; }
    public string note { get; set; } = "";
    public string? preview { get; set; }
    public string? spuId { get; set; }
    public string spuCode { get; set; } = "";
    public string spuName { get; set; } = "";
    public string? boardId { get; set; }
    public string boardName { get; set; } = "";
}
