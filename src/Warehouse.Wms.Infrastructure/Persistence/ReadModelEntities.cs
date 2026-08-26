namespace Warehouse.Wms.Infrastructure.Persistence;

public sealed class StatisticsBatchEntity
{
    public Guid Id { get; set; }
    public string BatchId { get; set; } = "";
    public string Period { get; set; } = "";
    public DateTimeOffset PeriodStart { get; set; }
    public DateTimeOffset PeriodEnd { get; set; }
    public DateTimeOffset GeneratedAt { get; set; }
    public string SourceVersion { get; set; } = "";
    public string? WarehouseCode { get; set; }
    public string Freshness { get; set; } = "Fresh";
    public decimal InventoryQuantity { get; set; }
    public decimal InventoryWeightKg { get; set; }
    public decimal LocationUtilizationPercent { get; set; }
    public decimal InboundQuantity { get; set; }
    public decimal OutboundQuantity { get; set; }
    public decimal TransferQuantity { get; set; }
    public decimal TaskSuccessRatePercent { get; set; }
    public int ExceptionCount { get; set; }
    public int StocktakingDifferenceCount { get; set; }
    public ICollection<StatisticsTrendEntity> Trends { get; set; } = new List<StatisticsTrendEntity>();
    public ICollection<StatisticsTaskStateEntity> TaskStates { get; set; } = new List<StatisticsTaskStateEntity>();
}
public sealed class StatisticsTrendEntity { public Guid Id { get; set; } public Guid BatchEntityId { get; set; } public DateTimeOffset PeriodStart { get; set; } public decimal InboundQuantity { get; set; } public decimal OutboundQuantity { get; set; } public decimal TransferQuantity { get; set; } public int ExceptionCount { get; set; } }
public sealed class StatisticsTaskStateEntity { public Guid Id { get; set; } public Guid BatchEntityId { get; set; } public string State { get; set; } = ""; public int Count { get; set; } }
public sealed class PointSnapshotEntity
{
    public Guid Id { get; set; }
    public string WarehouseCode { get; set; } = ""; public string ZoneCode { get; set; } = ""; public string Aisle { get; set; } = ""; public string Rack { get; set; } = ""; public int Level { get; set; } public string LocationCode { get; set; } = ""; public string Status { get; set; } = "";
    public string? PalletCode { get; set; } public string? MaterialCode { get; set; } public string? MaterialName { get; set; } public string? BatchNumber { get; set; } public decimal Quantity { get; set; } public decimal WeightKg { get; set; } public DateTimeOffset ObservedAt { get; set; } public long SourceVersion { get; set; } public bool IsLocked { get; set; } public string? LockReason { get; set; } public string? TaskState { get; set; } public string? LoadPoint { get; set; }
}
