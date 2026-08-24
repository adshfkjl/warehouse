namespace Warehouse.Wms.Domain.MasterData;

public sealed class Equipment
{
    private Equipment() { }

    public Equipment(string equipmentNumber, string name, string type, string? plcId = null)
    {
        EquipmentNumber = EntityValidation.Required(equipmentNumber, nameof(equipmentNumber));
        Name = EntityValidation.Required(name, nameof(name));
        Type = EntityValidation.Required(type, nameof(type));
        PlcId = plcId?.Trim();
    }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public string EquipmentNumber { get; private set; } = null!;
    public string Name { get; private set; } = null!;
    public string Type { get; private set; } = null!;
    public string? PlcId { get; private set; }
    public bool IsDisabled { get; private set; }
    public string? FieldMapping { get; private set; }
}
