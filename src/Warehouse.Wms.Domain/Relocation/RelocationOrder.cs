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
    public RelocationOrder(string orderNumber, Guid materialId, Guid palletId, Guid sourceLocationId, Guid destinationLocationId, decimal quantity, decimal weightKg, string? batchNumber = null, Guid? id = null)
    {
        if (string.IsNullOrWhiteSpace(orderNumber)) throw new ArgumentException("An order number is required.", nameof(orderNumber));
        if (materialId == Guid.Empty) throw new ArgumentException("A material is required.", nameof(materialId));
        if (palletId == Guid.Empty) throw new ArgumentException("A pallet is required.", nameof(palletId));
        if (sourceLocationId == Guid.Empty) throw new ArgumentException("A source location is required.", nameof(sourceLocationId));
        if (destinationLocationId == Guid.Empty) throw new ArgumentException("A destination location is required.", nameof(destinationLocationId));
        if (sourceLocationId == destinationLocationId) throw new InvalidOperationException("Source and destination locations must differ.");
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(quantity, 0m);
        ArgumentOutOfRangeException.ThrowIfNegative(weightKg);
        Id = id.GetValueOrDefault(Guid.NewGuid());
        if (Id == Guid.Empty) throw new ArgumentException("A relocation order id is required.", nameof(id));
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

    public void RestoreState(RelocationState target)
    {
        while (State != target)
        {
            var next = State switch
            {
                RelocationState.Draft => RelocationState.Allocated,
                RelocationState.Allocated => RelocationState.Queued,
                RelocationState.Queued when target is RelocationState.Executing => RelocationState.Executing,
                RelocationState.Queued when target is RelocationState.Completed => RelocationState.Executing,
                RelocationState.Queued when target is RelocationState.Failed => RelocationState.Failed,
                RelocationState.Queued when target is RelocationState.PhysicalStateUnknown => RelocationState.PhysicalStateUnknown,
                RelocationState.Queued => RelocationState.Exception,
                RelocationState.Executing when target is RelocationState.Completed => RelocationState.Completed,
                RelocationState.Executing when target is RelocationState.Failed => RelocationState.Failed,
                RelocationState.Executing when target is RelocationState.PhysicalStateUnknown => RelocationState.PhysicalStateUnknown,
                RelocationState.PhysicalStateUnknown when target is RelocationState.Completed => RelocationState.Completed,
                RelocationState.PhysicalStateUnknown when target is RelocationState.Failed => RelocationState.Failed,
                _ => throw new InvalidOperationException($"Relocation state '{State}' cannot be restored to '{target}'.")
            };
            TransitionTo(next);
        }
    }
}
