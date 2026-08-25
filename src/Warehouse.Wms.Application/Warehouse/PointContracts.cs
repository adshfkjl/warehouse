namespace Warehouse.Wms.Application.Points;

public sealed class PointQuery
{
    public string? WarehouseCode { get; init; }
    public string? ZoneCode { get; init; }
    public string? Aisle { get; init; }
    public string? Rack { get; init; }
    public int? Level { get; init; }
    public string? LocationCode { get; init; }
    public string? Status { get; init; }
    public string? MaterialCode { get; init; }
    public string? PalletCode { get; init; }
}

public sealed record WarehousePointSnapshot(
    string WarehouseCode,
    string ZoneCode,
    string Aisle,
    string Rack,
    int Level,
    string LocationCode,
    string Status,
    string? PalletCode,
    string? MaterialCode,
    string? MaterialName,
    string? BatchNumber,
    decimal Quantity,
    decimal WeightKg,
    DateTimeOffset ObservedAt,
    long SourceVersion,
    bool IsLocked,
    string? LockReason,
    string? TaskState,
    string? LoadPoint,
    string Freshness = "Fresh");

public sealed record PalletPosition(
    string PalletCode,
    string CurrentLocationCode,
    string? MaterialCode,
    decimal Quantity,
    decimal WeightKg,
    string Status,
    DateTimeOffset ObservedAt,
    string Freshness);

public interface IPointReadModel
{
    IReadOnlyCollection<WarehousePointSnapshot> Query(PointQuery? query = null, DateTimeOffset? now = null);
    WarehousePointSnapshot? GetByLocation(string locationCode, DateTimeOffset? now = null);
    PalletPosition? FindPallet(string palletCode, DateTimeOffset? now = null);
}
