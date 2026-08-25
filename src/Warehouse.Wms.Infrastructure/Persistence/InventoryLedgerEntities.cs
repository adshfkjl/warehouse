using Warehouse.Wms.Domain.Inventory;

namespace Warehouse.Wms.Infrastructure.Persistence;

public sealed class InventoryBalanceEntity
{
    public Guid Id { get; set; }
    public string BalanceKey { get; set; } = null!;
    public Guid MaterialId { get; set; }
    public Guid? PalletId { get; set; }
    public Guid? LocationId { get; set; }
    public string? BatchNumber { get; set; }
    public decimal Quantity { get; set; }
    public decimal WeightKg { get; set; }
    public InventoryStatus Status { get; set; }
    public int Version { get; set; }
}

public sealed class InventoryTransactionEntity
{
    public Guid Id { get; set; }
    public string IdempotencyKey { get; set; } = null!;
    public InventoryTransactionType Type { get; set; }
    public Guid MaterialId { get; set; }
    public Guid? PalletId { get; set; }
    public Guid? LocationId { get; set; }
    public Guid? SourceLocationId { get; set; }
    public Guid? DestinationLocationId { get; set; }
    public string? BatchNumber { get; set; }
    public decimal Quantity { get; set; }
    public decimal WeightKg { get; set; }
    public InventoryStatus StatusBefore { get; set; }
    public InventoryStatus StatusAfter { get; set; }
    public string Fingerprint { get; set; } = null!;
    public string? SourceDocumentId { get; set; }
    public string? TaskNumber { get; set; }
    public string? OperatorId { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}
