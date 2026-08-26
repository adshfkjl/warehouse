using Microsoft.EntityFrameworkCore;
using Warehouse.Wms.Application.Reports;
using Warehouse.Wms.Domain.Inventory;
using Warehouse.Wms.Domain.Tasks;
using Warehouse.Wms.Infrastructure.Persistence;

namespace Warehouse.Wms.Infrastructure.Reports;

public sealed record InventoryStatisticsFact(decimal Quantity, decimal WeightKg, InventoryStatus Status, Guid? LocationId);
public sealed record TransactionStatisticsFact(InventoryTransactionType Type, decimal Quantity, DateTimeOffset OccurredAt, Guid? LocationId = null, Guid? SourceLocationId = null, Guid? DestinationLocationId = null);
public sealed record TaskStatisticsFact(Guid TaskId, TaskState State, DateTimeOffset UpdatedAt, string? WarehouseCode = null, bool WarehouseResolved = false);
public sealed record LocationStatisticsFact(decimal Capacity, decimal OccupiedQuantity);

public sealed class SqlServerStatisticsSource(IDbContextFactory<WarehouseDbContext> factory, string? warehouseCode = null) : IStatisticsSource
{
    public async Task<StatisticsBatchRequest?> BuildAsync(StatisticsPeriod period, DateTimeOffset periodStart, DateTimeOffset periodEnd, string sourceVersion, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var locationRowsQuery = from l in db.Locations.AsNoTracking()
                                join r in db.Racks.AsNoTracking() on l.RackId equals r.Id
                                join a in db.Aisles.AsNoTracking() on r.AisleId equals a.Id
                                join z in db.Zones.AsNoTracking() on a.ZoneId equals z.Id
                                join w in db.Warehouses.AsNoTracking() on z.WarehouseId equals w.Id
                                where string.IsNullOrWhiteSpace(warehouseCode) || w.Code == warehouseCode.Trim()
                                select new { l.Id, l.Capacity };
        var locationRows = await locationRowsQuery.ToListAsync(cancellationToken);
        var locationIds = locationRows.Select(x => x.Id).ToHashSet();
        var balances = await db.InventoryBalances.AsNoTracking().Where(x => string.IsNullOrWhiteSpace(warehouseCode) || (x.LocationId != null && locationIds.Contains(x.LocationId.Value))).Select(x => new InventoryStatisticsFact(x.Quantity, x.WeightKg, x.Status, x.LocationId)).ToListAsync(cancellationToken);
        var transactions = await db.InventoryTransactions.AsNoTracking().Where(x => x.OccurredAt >= periodStart && x.OccurredAt < periodEnd && (string.IsNullOrWhiteSpace(warehouseCode) || (x.LocationId != null && locationIds.Contains(x.LocationId.Value)) || (x.Type == InventoryTransactionType.Move && ((x.SourceLocationId != null && locationIds.Contains(x.SourceLocationId.Value)) || (x.DestinationLocationId != null && locationIds.Contains(x.DestinationLocationId.Value)))))).Select(x => new TransactionStatisticsFact(x.Type, x.Quantity, x.OccurredAt, x.LocationId, x.SourceLocationId, x.DestinationLocationId)).ToListAsync(cancellationToken);
        var taskRows = await db.Tasks.AsNoTracking().Where(x => x.UpdatedAt >= periodStart && x.UpdatedAt < periodEnd).Select(x => new { x.Id, x.State, x.UpdatedAt, x.DispatchContextJson }).ToListAsync(cancellationToken);
        var locationWarehouse = await (from l in db.Locations.AsNoTracking() join r in db.Racks.AsNoTracking() on l.RackId equals r.Id join a in db.Aisles.AsNoTracking() on r.AisleId equals a.Id join z in db.Zones.AsNoTracking() on a.ZoneId equals z.Id join w in db.Warehouses.AsNoTracking() on z.WarehouseId equals w.Id select new { l.Code, WarehouseCode = w.Code }).ToDictionaryAsync(x => x.Code, x => x.WarehouseCode, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var tasks = taskRows.Select(x => TaskFact(x.Id, x.State, x.UpdatedAt, x.DispatchContextJson, locationWarehouse)).ToArray();
        var locations = locationRows.Select(x => new LocationStatisticsFact(x.Capacity, balances.Where(b => b.LocationId == x.Id).Sum(b => b.Quantity))).ToArray();
        return StatisticsAggregation.Build(period, periodStart, periodEnd, sourceVersion, balances, transactions, tasks, locations, warehouseCode?.Trim());
    }

    private static TaskStatisticsFact TaskFact(Guid id, TaskState state, DateTimeOffset updatedAt, string? context, Dictionary<string, string> locations)
    {
        if (string.IsNullOrWhiteSpace(context)) return new TaskStatisticsFact(id, state, updatedAt);
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(context);
            var root = json.RootElement;
            var codes = new[] { "SourceLocation", "sourceLocation", "DestinationLocation", "destinationLocation" }.Select(k => root.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
            var warehouses = codes.Where(c => locations.ContainsKey(c!)).Select(c => locations[c!]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return warehouses.Length == 1 ? new TaskStatisticsFact(id, state, updatedAt, warehouses[0], true) : new TaskStatisticsFact(id, state, updatedAt);
        }
        catch (System.Text.Json.JsonException) { return new TaskStatisticsFact(id, state, updatedAt); }
    }
}

public static class StatisticsAggregation
{
    public static StatisticsBatchRequest? Build(StatisticsPeriod period, DateTimeOffset start, DateTimeOffset end, string sourceVersion, IReadOnlyCollection<InventoryStatisticsFact> balances, IReadOnlyCollection<TransactionStatisticsFact> transactions, IReadOnlyCollection<TaskStatisticsFact> tasks, IReadOnlyCollection<LocationStatisticsFact>? locations = null, string? warehouseCode = null)
    {
        if (balances.Count == 0 && transactions.Count == 0 && tasks.Count == 0) return null;
        var inbound = transactions.Where(x => x.Type == InventoryTransactionType.Increase).Sum(x => x.Quantity);
        var outbound = transactions.Where(x => x.Type == InventoryTransactionType.Decrease).Sum(x => x.Quantity);
        var transfer = transactions.Where(x => x.Type == InventoryTransactionType.Move).Sum(x => x.Quantity);
        var latestTasks = tasks.GroupBy(x => x.TaskId).Select(x => x.OrderByDescending(y => y.UpdatedAt).First()).Where(x => string.IsNullOrWhiteSpace(warehouseCode) || (x.WarehouseResolved && string.Equals(x.WarehouseCode, warehouseCode, StringComparison.OrdinalIgnoreCase))).ToArray();
        var completed = latestTasks.Count(x => x.State == TaskState.Succeeded);
        var terminal = latestTasks.Where(x => x.State is TaskState.Succeeded or TaskState.Failed or TaskState.TimedOut or TaskState.Canceled or TaskState.ManualIntervention).ToArray();
        var successRate = terminal.Length == 0 ? 0m : terminal.Count(x => x.State == TaskState.Succeeded) * 100m / terminal.Length;
        var utilization = locations is null || locations.Count == 0 ? 0m : locations.Sum(x => x.Capacity) == 0 ? 0m : locations.Sum(x => x.OccupiedQuantity) * 100m / locations.Sum(x => x.Capacity);
        var kpi = new StatisticsKpi(balances.Sum(x => x.Quantity), balances.Sum(x => x.WeightKg), utilization, inbound, outbound, transfer, successRate, terminal.Count(x => x.State is TaskState.Failed or TaskState.TimedOut or TaskState.ManualIntervention), 0);
        var trend = new StatisticsTrendPoint(start, inbound, outbound, transfer, kpi.ExceptionCount);
        var states = terminal.GroupBy(x => x.State.ToString()).Select(x => new TaskStateCount(x.Key, x.Count())).ToArray();
        return new StatisticsBatchRequest(period, start, end, sourceVersion, warehouseCode, kpi, [trend], states);
    }
}
