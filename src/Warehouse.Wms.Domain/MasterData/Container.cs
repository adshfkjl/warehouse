namespace Warehouse.Wms.Domain.MasterData;

public sealed class Container
{
    private Container() { }

    public Container(string code, string type)
    {
        Code = EntityValidation.Required(code, nameof(code));
        Type = EntityValidation.Required(type, nameof(type));
    }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public string Code { get; private set; } = null!;
    public string Type { get; private set; } = null!;
    public bool IsDisabled { get; private set; }
}
