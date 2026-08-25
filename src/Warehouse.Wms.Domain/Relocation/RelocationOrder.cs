namespace Warehouse.Wms.Domain.Relocation;

public enum RelocationState
{
    Draft,
    Allocated,
    Queued,
    Executing,
    Completed,
    Canceled,
    Failed,
    PhysicalStateUnknown,
    Exception
}

public sealed class RelocationOrder
{
    public RelocationOrder(string orderNumber, Guid materialId, Guid palletId, Guid sourceLocationId, Guid destinationLocationId, decimal quantity, decimal weightKg, string? batchNumber = null)
    {
        if (string.IsNullOrWhiteSpace(orderNumber)) throw new ArgumentException("An order number is required.", nameof(orderNumber));
        if (materialId == Guid.Empty) throw new ArgumentException("A material is required.", nameof(materialId));
        if (palletId == Guid.Empty) throw new ArgumentException("A pallet is required.", nameof(palletId));
        if (sourceLocationId == Guid.Empty) throw new ArgumentException("A source location is required.", nameof(sourceLocationId));
        if (destinationLocationId == Guid.Empty) throw new ArgumentException("A destination location is required.", nameof(destinationLocationId));
        if (sourceLocationId == destinationLocationId) throw new InvalidOperationException("Source and destination locations must differ.");
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(quantity, 0m);
        ArgumentOutOfRangeException.ThrowIfNegative(weightKg);
        Id = Guid.NewGuid();
        OrderNumber = orderNumber.Trim();
        MaterialId = materialId;
        PalletId = palletId;
        SourceLocationId = sourceLocationId;
        DestinationLocationId = destinationLocationId;
        Quantity = quantity;
        WeightKg = weightKg;
        BatchNumber = string.IsNullOrWhiteSpace(batchNumber) ? null : batchNumber.Trim();
    }

    public Guid Id { get; }
    public string OrderNumber { get; }
    public Guid MaterialId { get; }
    public Guid PalletId { get; }
    public Guid SourceLocationId { get; }
    public Guid DestinationLocationId { get; }
    public decimal Quantity { get; }
    public decimal WeightKg { get; }
    public string? BatchNumber { get; }
    public RelocationState State { get; private set; } = RelocationState.Draft;

    public void TransitionTo(RelocationState next)
    {
        var allowed = State switch
        {
            RelocationState.Draft => next is RelocationState.Allocated or RelocationState.Canceled,
            RelocationState.Allocated => next is RelocationState.Queued or RelocationState.Canceled or RelocationState.Exception,
            RelocationState.Queued => next is RelocationState.Executing or RelocationState.Completed or RelocationState.Failed or RelocationState.PhysicalStateUnknown or RelocationState.Exception,
            RelocationState.Executing => next is RelocationState.Completed or RelocationState.Failed or RelocationState.PhysicalStateUnknown or RelocationState.Exception,
            RelocationState.PhysicalStateUnknown => next is RelocationState.Completed or RelocationState.Failed or RelocationState.Exception,
            _ => false
        };
        if (!allowed) throw new InvalidOperationException($"Relocation state transition '{State}' -> '{next}' is not allowed.");
        State = next;
    }
}
