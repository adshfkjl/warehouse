namespace Warehouse.Wms.Domain.MasterData;

public sealed class LoadingPoint
{
    private LoadingPoint() { }

    public LoadingPoint(string code, string name, string? fieldMapping = null)
        : this(Guid.NewGuid(), code, name, fieldMapping)
    {
    }

    public LoadingPoint(Guid id, string code, string name, string? fieldMapping = null)
    {
        if (id == Guid.Empty) throw new ArgumentException("A loading point id is required.", nameof(id));
        Id = id;
        Code = EntityValidation.Required(code, nameof(code));
        Name = EntityValidation.Required(name, nameof(name));
        FieldMapping = fieldMapping?.Trim();
    }

    public Guid Id { get; private set; }
    public string Code { get; private set; } = null!;
    public string Name { get; private set; } = null!;
    public bool IsDisabled { get; private set; }
    public bool IsLocked { get; private set; }
    public string? FieldMapping { get; private set; }
}
