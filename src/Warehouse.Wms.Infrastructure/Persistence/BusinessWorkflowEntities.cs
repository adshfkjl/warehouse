namespace Warehouse.Wms.Infrastructure.Persistence;

public sealed class BusinessWorkflowEntity
{
    public Guid Id { get; set; }
    public string AggregateType { get; set; } = null!;
    public string AggregateKey { get; set; } = null!;
    public int Version { get; set; }
    public string Status { get; set; } = null!;
    public string SnapshotJson { get; set; } = null!;
    public DateTimeOffset UpdatedAt { get; set; }
    public string? WarehouseTaskNumber { get; set; }
}

public sealed class BusinessWorkflowStateHistoryEntity
{
    public Guid Id { get; set; }
    public string AggregateType { get; set; } = null!;
    public string AggregateKey { get; set; } = null!;
    public int Version { get; set; }
    public string? FromStatus { get; set; }
    public string ToStatus { get; set; } = null!;
    public DateTimeOffset OccurredAt { get; set; }
    public string? Reason { get; set; }
    public string? OperatorId { get; set; }
}

public sealed class BusinessWorkflowIdempotencyEntity
{
    public Guid Id { get; set; }
    public string Scope { get; set; } = null!;
    public string Key { get; set; } = null!;
    public string RequestHash { get; set; } = null!;
    public string? AggregateType { get; set; }
    public string? AggregateKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
