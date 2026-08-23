using System.Text.Json.Serialization;

// Models/InventoryQuery.cs
public class InventoryQuery
{
    public string? PRD_NO { get; set; }      // 货品编号
    public string? PRD_NAME { get; set; }    // 货品名称
    public string? PRD_SPC { get; set; }     // 货品规格
    public string? WH { get; set; }          // 仓库代号
    public string? POS { get; set; }         // 位置信息
}

// Models/InventoryInfo.cs
public class InventoryInfo
{
    public string WH { get; set; } = string.Empty;        // 仓库代号
    public string PRD_NO { get; set; } = string.Empty;    // 货品编号
    public string NAME { get; set; } = string.Empty;      // 货品名称
    public string PRD_MARK { get; set; } = string.Empty;  // 货品标记
    public string SPC { get; set; } = string.Empty;       // 规格
    public string UT { get; set; } = string.Empty;        // 单位
    public double QTY { get; set; }                       // 数量
    public string REM { get; set; } = string.Empty;       // 备注/位置信息

    [JsonPropertyName("LoadPoint")]
    public int LoadPoint { get; set; } = 0;               // 装载点
}
