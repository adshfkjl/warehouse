namespace Warehouse.Wms.Domain.MasterData;

public sealed class Zone
{
    private Zone() { }

    public Zone(Guid warehouseId, string code, string name)
    {
        WarehouseId = warehouseId;
        Code = EntityValidation.Required(code, nameof(code));
        Name = EntityValidation.Required(name, nameof(name));
    }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid WarehouseId { get; private set; }
    public string Code { get; private set; } = null!;
    public string Name { get; private set; } = null!;
    public bool IsDisabled { get; private set; }
}
