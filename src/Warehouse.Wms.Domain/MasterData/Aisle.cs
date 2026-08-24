namespace Warehouse.Wms.Domain.MasterData;

public sealed class Aisle
{
    private Aisle() { }

    public Aisle(Guid zoneId, string code, string name)
    {
        ZoneId = zoneId;
        Code = EntityValidation.Required(code, nameof(code));
        Name = EntityValidation.Required(name, nameof(name));
    }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid ZoneId { get; private set; }
    public string Code { get; private set; } = null!;
    public string Name { get; private set; } = null!;
    public bool IsDisabled { get; private set; }
}
