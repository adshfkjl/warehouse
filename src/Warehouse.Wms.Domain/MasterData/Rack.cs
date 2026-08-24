namespace Warehouse.Wms.Domain.MasterData;

public sealed class Rack
{
    private Rack() { }

    public Rack(Guid aisleId, string code, string name)
    {
        AisleId = aisleId;
        Code = EntityValidation.Required(code, nameof(code));
        Name = EntityValidation.Required(name, nameof(name));
    }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid AisleId { get; private set; }
    public string Code { get; private set; } = null!;
    public string Name { get; private set; } = null!;
    public bool IsDisabled { get; private set; }
}
