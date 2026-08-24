namespace Warehouse.Wms.Domain.Inventory;

public sealed record InventoryOperationContext
{
    public InventoryOperationContext(
        string idempotencyKey,
        string? sourceDocumentId = null,
        string? taskNumber = null,
        string? operatorId = null,
        string? reason = null)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new ArgumentException("An idempotency key is required.", nameof(idempotencyKey));
        }

        IdempotencyKey = idempotencyKey.Trim();
        SourceDocumentId = Normalize(sourceDocumentId);
        TaskNumber = Normalize(taskNumber);
        OperatorId = Normalize(operatorId);
        Reason = Normalize(reason);
    }

    public string IdempotencyKey { get; }

    public string? SourceDocumentId { get; }

    public string? TaskNumber { get; }

    public string? OperatorId { get; }

    public string? Reason { get; }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class InventoryTransaction
{
    public InventoryTransaction(
        Guid id,
        InventoryOperationContext context,
        InventoryTransactionType type,
        Guid materialId,
        Guid? palletId,
        Guid? locationId,
        Guid? sourceLocationId,
        Guid? destinationLocationId,
        string? batchNumber,
        decimal quantity,
        decimal weightKg,
        InventoryStatus statusBefore,
        InventoryStatus statusAfter,
        string fingerprint,
        DateTimeOffset occurredAt)
    {
        Id = id;
        Context = context ?? throw new ArgumentNullException(nameof(context));
        Type = type;
        MaterialId = materialId;
        PalletId = palletId;
        LocationId = locationId;
        SourceLocationId = sourceLocationId;
        DestinationLocationId = destinationLocationId;
        BatchNumber = string.IsNullOrWhiteSpace(batchNumber) ? null : batchNumber.Trim();
        Quantity = quantity;
        WeightKg = weightKg;
        StatusBefore = statusBefore;
        StatusAfter = statusAfter;
        Fingerprint = fingerprint ?? throw new ArgumentNullException(nameof(fingerprint));
        OccurredAt = occurredAt;
    }

    public Guid Id { get; }

    public string IdempotencyKey => Context.IdempotencyKey;

    public InventoryOperationContext Context { get; }

    public InventoryTransactionType Type { get; }

    public Guid MaterialId { get; }

    public Guid? PalletId { get; }

    public Guid? LocationId { get; }

    public Guid? SourceLocationId { get; }

    public Guid? DestinationLocationId { get; }

    public string? BatchNumber { get; }

    public decimal Quantity { get; }

    public decimal WeightKg { get; }

    public InventoryStatus StatusBefore { get; }

    public InventoryStatus StatusAfter { get; }

    public string Fingerprint { get; }

    public DateTimeOffset OccurredAt { get; }

    public string? SourceDocumentId => Context.SourceDocumentId;

    public string? TaskNumber => Context.TaskNumber;

    public string? OperatorId => Context.OperatorId;

    public string? Reason => Context.Reason;
}
