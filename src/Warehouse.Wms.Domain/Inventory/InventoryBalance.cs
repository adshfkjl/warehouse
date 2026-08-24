namespace Warehouse.Wms.Domain.Inventory;

public sealed class InventoryBalance
{
    public InventoryBalance(
        Guid materialId,
        Guid? palletId,
        Guid? locationId,
        string? batchNumber,
        decimal quantity,
        decimal weightKg,
        InventoryStatus status,
        int version = 1)
    {
        if (materialId == Guid.Empty)
        {
            throw new ArgumentException("A material is required.", nameof(materialId));
        }

        if (quantity < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), quantity, "Quantity cannot be negative.");
        }

        if (weightKg < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(weightKg), weightKg, "Weight cannot be negative.");
        }

        if (version < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(version), version, "Version must be positive.");
        }

        MaterialId = materialId;
        PalletId = palletId;
        LocationId = locationId;
        BatchNumber = string.IsNullOrWhiteSpace(batchNumber) ? null : batchNumber.Trim();
        Quantity = quantity;
        WeightKg = weightKg;
        Status = status;
        Version = version;
    }

    public Guid MaterialId { get; }

    public Guid? PalletId { get; }

    public Guid? LocationId { get; }

    public string? BatchNumber { get; }

    public decimal Quantity { get; }

    public decimal WeightKg { get; }

    public InventoryStatus Status { get; }

    public int Version { get; }

    public string Key => string.Join(
        ":",
        MaterialId,
        PalletId?.ToString() ?? "-",
        LocationId?.ToString() ?? "-",
        BatchNumber ?? "-");
}
