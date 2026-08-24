namespace Warehouse.Wms.Domain.MasterData;

public sealed class Location
{
    private Location() { }

    public Location(
        Guid rackId,
        string code,
        int capacity,
        decimal maxWeightKg,
        decimal lengthMm,
        decimal widthMm,
        decimal heightMm,
        string? fieldMapping = null)
    {
        RackId = rackId;
        Code = EntityValidation.Required(code, nameof(code));
        Capacity = EntityValidation.Positive(capacity, nameof(capacity));
        MaxWeightKg = EntityValidation.Positive(maxWeightKg, nameof(maxWeightKg));
        LengthMm = EntityValidation.Positive(lengthMm, nameof(lengthMm));
        WidthMm = EntityValidation.Positive(widthMm, nameof(widthMm));
        HeightMm = EntityValidation.Positive(heightMm, nameof(heightMm));
        FieldMapping = fieldMapping?.Trim();
    }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid RackId { get; private set; }
    public string Code { get; private set; } = null!;
    public int Capacity { get; private set; }
    public decimal MaxWeightKg { get; private set; }
    public decimal LengthMm { get; private set; }
    public decimal WidthMm { get; private set; }
    public decimal HeightMm { get; private set; }
    public bool IsDisabled { get; private set; }
    public bool IsLocked { get; private set; }
    public string? FieldMapping { get; private set; }
}
