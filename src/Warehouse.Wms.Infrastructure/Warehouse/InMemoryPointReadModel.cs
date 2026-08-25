using Warehouse.Wms.Application.Points;

namespace Warehouse.Wms.Infrastructure.Warehouse;

public sealed class InMemoryPointReadModel : IPointReadModel
{
    private readonly Dictionary<string, WarehousePointSnapshot> _points;
    private readonly TimeSpan _freshnessThreshold;

    public InMemoryPointReadModel(IEnumerable<WarehousePointSnapshot>? points = null, TimeSpan? freshnessThreshold = null)
    {
        _points = (points ?? Array.Empty<WarehousePointSnapshot>()).GroupBy(p => p.LocationCode, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.SourceVersion).First(), StringComparer.OrdinalIgnoreCase);
        _freshnessThreshold = freshnessThreshold ?? TimeSpan.FromMinutes(2);
    }

    public IReadOnlyCollection<WarehousePointSnapshot> Query(PointQuery? query = null, DateTimeOffset? now = null)
    {
        query ??= new PointQuery();
        var current = now ?? DateTimeOffset.UtcNow;
        return _points.Values.Where(p => Matches(p, query)).Select(p => WithFreshness(p, current)).ToArray();
    }

    public WarehousePointSnapshot? GetByLocation(string locationCode, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(locationCode)) return null;
        return _points.TryGetValue(locationCode.Trim(), out var point)
            ? WithFreshness(point, now ?? DateTimeOffset.UtcNow)
            : null;
    }

    public PalletPosition? FindPallet(string palletCode, DateTimeOffset? now = null)
    {
        var point = _points.Values.FirstOrDefault(p => string.Equals(p.PalletCode, palletCode.Trim(), StringComparison.OrdinalIgnoreCase));
        if (point is null) return null;
        var observed = WithFreshness(point, now ?? DateTimeOffset.UtcNow);
        return new PalletPosition(observed.PalletCode!, observed.LocationCode, observed.MaterialCode, observed.Quantity, observed.WeightKg, observed.Status, observed.ObservedAt, observed.Freshness);
    }

    private WarehousePointSnapshot WithFreshness(WarehousePointSnapshot p, DateTimeOffset now)
    {
        var freshness = p.Status is "Offline"
            ? "Offline"
            : now - p.ObservedAt > _freshnessThreshold ? "Stale" : "Fresh";
        return p with { Freshness = freshness };
    }

    private static bool Matches(WarehousePointSnapshot p, PointQuery q)
        => (q.WarehouseCode is null || p.WarehouseCode.Equals(q.WarehouseCode, StringComparison.OrdinalIgnoreCase))
        && (q.ZoneCode is null || p.ZoneCode.Equals(q.ZoneCode, StringComparison.OrdinalIgnoreCase))
        && (q.Aisle is null || p.Aisle.Equals(q.Aisle, StringComparison.OrdinalIgnoreCase))
        && (q.Rack is null || p.Rack.Equals(q.Rack, StringComparison.OrdinalIgnoreCase))
        && (q.Level is null || p.Level == q.Level)
        && (q.LocationCode is null || p.LocationCode.Equals(q.LocationCode, StringComparison.OrdinalIgnoreCase))
        && (q.Status is null || p.Status.Equals(q.Status, StringComparison.OrdinalIgnoreCase))
        && (q.MaterialCode is null || p.MaterialCode?.Equals(q.MaterialCode, StringComparison.OrdinalIgnoreCase) == true)
        && (q.PalletCode is null || p.PalletCode?.Equals(q.PalletCode, StringComparison.OrdinalIgnoreCase) == true);
}
