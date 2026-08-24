namespace Warehouse.Wms.Domain.MasterData;

public enum PalletOwnershipStatus
{
    Available,
    InUse,
    Quarantined,
    Retired
}

public sealed class Pallet
{
    private Pallet() { }

    public Pallet(string code, string? type = null)
    {
        Code = EntityValidation.Required(code, nameof(code));
        Type = type?.Trim();
    }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public string Code { get; private set; } = null!;
    public string? Type { get; private set; }
    public int Version { get; private set; } = 1;
    public PalletOwnershipStatus OwnershipStatus { get; private set; } = PalletOwnershipStatus.Available;
    public Guid? CurrentLocationId { get; private set; }
    public Guid? CurrentLoadingPointId { get; private set; }
    public bool IsDisabled { get; private set; }
}
