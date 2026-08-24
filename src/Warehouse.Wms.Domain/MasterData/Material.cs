namespace Warehouse.Wms.Domain.MasterData;

public sealed class Material
{
    private Material() { }

    public Material(string code, string name, decimal? unitWeightKg = null)
    {
        Code = EntityValidation.Required(code, nameof(code));
        Name = EntityValidation.Required(name, nameof(name));
        if (unitWeightKg is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(unitWeightKg), unitWeightKg, "Value must be greater than zero.");
        }

        UnitWeightKg = unitWeightKg;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public string Code { get; private set; } = null!;
    public string Name { get; private set; } = null!;
    public decimal? UnitWeightKg { get; private set; }
    public bool IsDisabled { get; private set; }
}
