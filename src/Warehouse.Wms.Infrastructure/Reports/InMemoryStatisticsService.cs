using Warehouse.Wms.Application.Reports;
using System.Security.Cryptography;
using System.Text;

namespace Warehouse.Wms.Infrastructure.Reports;

public sealed class InMemoryStatisticsService : IStatisticsService
{
    private readonly Dictionary<string, StatisticsSnapshot> _batches = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    public StatisticsSnapshot? LatestSuccessful { get; private set; }

    public Task<StatisticsSnapshot> GenerateAsync(StatisticsPeriod period, DateTimeOffset periodStart, DateTimeOffset periodEnd, string sourceVersion, bool fail = false, CancellationToken cancellationToken = default)
        => GenerateAsync(new StatisticsBatchRequest(period, periodStart, periodEnd, sourceVersion), fail, cancellationToken);

    public Task<StatisticsSnapshot> GenerateAsync(StatisticsBatchRequest request, bool fail = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.PeriodEnd <= request.PeriodStart) throw new ArgumentException("periodEnd must be after periodStart");
        if (string.IsNullOrWhiteSpace(request.SourceVersion)) throw new ArgumentException("sourceVersion is required");
        var key = $"{request.Period}:{request.PeriodStart.UtcDateTime:O}:{request.PeriodEnd.UtcDateTime:O}:{request.WarehouseCode?.Trim() ?? "*"}";
        lock (_gate)
        {
            if (_batches.TryGetValue(key, out var existing))
            {
                if (!string.Equals(existing.SourceVersion, request.SourceVersion.Trim(), StringComparison.Ordinal))
                    throw new InvalidOperationException("A statistics batch already exists for this period and source version.");
                return Task.FromResult(existing);
            }
            if (fail) throw new InvalidOperationException("Statistics generation failed");
            cancellationToken.ThrowIfCancellationRequested();
            var batchId = CreateStableBatchId(key);
            var snapshot = new StatisticsSnapshot(
                batchId, request.Period, request.PeriodStart, request.PeriodEnd, DateTimeOffset.UtcNow,
                request.SourceVersion.Trim(), "Fresh",
                request.Kpi ?? new StatisticsKpi(0, 0, 0, 0, 0, 0, 100, 0, 0),
                request.Trends ?? Array.Empty<StatisticsTrendPoint>(),
                request.TaskStates ?? Array.Empty<TaskStateCount>(), request.WarehouseCode?.Trim());
            _batches[key] = snapshot;
            LatestSuccessful = snapshot;
            return Task.FromResult(snapshot);
        }
    }

    public StatisticsSnapshot GetSummary(StatisticsPeriod? period = null, string? warehouseCode = null)
    {
        lock (_gate)
        {
            var requestedWarehouse = string.IsNullOrWhiteSpace(warehouseCode) ? null : warehouseCode.Trim();
            var scoped = _batches.Values
                .Where(batch => (period is null || batch.Period == period)
                    && (requestedWarehouse is null || string.Equals(batch.WarehouseCode, requestedWarehouse, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(batch => batch.GeneratedAt)
                .FirstOrDefault();
            if (scoped is not null) return scoped;
            var end = DateTimeOffset.UtcNow;
            var start = end.Date.AddDays(-1);
            return new StatisticsSnapshot("STAT-EMPTY", period ?? StatisticsPeriod.Day, start, end, end, "simulated-v1", "Delayed",
                new StatisticsKpi(0, 0, 0, 0, 0, 0, 0, 0, 0), Array.Empty<StatisticsTrendPoint>(), Array.Empty<TaskStateCount>(), requestedWarehouse);
        }
    }

    private static string CreateStableBatchId(string key)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return $"STAT-{Convert.ToHexString(digest)[..16]}";
    }
}
