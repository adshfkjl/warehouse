namespace Warehouse.Wms.Domain.MasterData;

public sealed class Warehouse
{
    private Warehouse() { }

    public Warehouse(string code, string name)
    {
        Code = EntityValidation.Required(code, nameof(code));
        Name = EntityValidation.Required(name, nameof(name));
    }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public string Code { get; private set; } = null!;
    public string Name { get; private set; } = null!;
    public bool IsDisabled { get; private set; }
    public string? FieldMapping { get; private set; }
}
