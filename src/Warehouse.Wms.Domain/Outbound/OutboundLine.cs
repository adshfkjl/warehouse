namespace Warehouse.Wms.Domain.Outbound;

public sealed class OutboundLine
{
    private OutboundLine() { }

    public OutboundLine(Guid materialId, decimal requestedQuantity, string? batchNumber = null, Guid? palletId = null, Guid? locationId = null)
    {
        if (materialId == Guid.Empty) throw new ArgumentException("A material is required.", nameof(materialId));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(requestedQuantity, 0m);
        Id = Guid.NewGuid();
        MaterialId = materialId;
        RequestedQuantity = requestedQuantity;
        BatchNumber = string.IsNullOrWhiteSpace(batchNumber) ? null : batchNumber.Trim();
        PalletId = palletId;
        LocationId = locationId;
    }

    public Guid Id { get; private set; }
    public Guid MaterialId { get; private set; }
    public decimal RequestedQuantity { get; private set; }
    public string? BatchNumber { get; private set; }
    public Guid? PalletId { get; private set; }
    public Guid? LocationId { get; private set; }
}
