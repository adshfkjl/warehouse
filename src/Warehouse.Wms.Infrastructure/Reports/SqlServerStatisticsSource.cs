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
        var normalizedWarehouseCode = string.IsNullOrWhiteSpace(warehouseCode) ? null : warehouseCode.Trim();
        var locationRowsQuery = from l in db.Locations.AsNoTracking()
                                join r in db.Racks.AsNoTracking() on l.RackId equals r.Id
                                join a in db.Aisles.AsNoTracking() on r.AisleId equals a.Id
                                join z in db.Zones.AsNoTracking() on a.ZoneId equals z.Id
                                join w in db.Warehouses.AsNoTracking() on z.WarehouseId equals w.Id
                                select new { l.Id, l.Code, l.Capacity, WarehouseCode = w.Code };
        var scopedLocationIds = normalizedWarehouseCode is null
            ? locationRowsQuery.Select(x => x.Id)
            : locationRowsQuery.Where(x => x.WarehouseCode == normalizedWarehouseCode).Select(x => x.Id);

        var balancesQuery = db.InventoryBalances.AsNoTracking();
        if (normalizedWarehouseCode is not null)
        {
            balancesQuery = balancesQuery.Where(x => x.LocationId != null && scopedLocationIds.Contains(x.LocationId.Value));
        }
        var balanceTotal = await balancesQuery
            .GroupBy(_ => 1)
            .Select(group => new { Quantity = group.Sum(x => x.Quantity), WeightKg = group.Sum(x => x.WeightKg) })
            .SingleOrDefaultAsync(cancellationToken);
        IReadOnlyCollection<InventoryStatisticsFact> balances = balanceTotal is null
            ? []
            : [new InventoryStatisticsFact(balanceTotal.Quantity, balanceTotal.WeightKg, InventoryStatus.Available, null)];

        var occupiedQuantity = await db.InventoryBalances.AsNoTracking()
            .Where(x => x.LocationId != null && scopedLocationIds.Contains(x.LocationId.Value))
            .GroupBy(_ => 1)
            .Select(group => (decimal?)group.Sum(x => x.Quantity))
            .SingleOrDefaultAsync(cancellationToken) ?? 0m;
        var capacity = await (normalizedWarehouseCode is null ? locationRowsQuery : locationRowsQuery.Where(x => x.WarehouseCode == normalizedWarehouseCode))
            .GroupBy(_ => 1)
            .Select(group => (decimal?)group.Sum(x => (decimal)x.Capacity))
            .SingleOrDefaultAsync(cancellationToken) ?? 0m;
        IReadOnlyCollection<LocationStatisticsFact> locations = [new LocationStatisticsFact(capacity, occupiedQuantity)];

        var transactionsQuery = db.InventoryTransactions.AsNoTracking()
            .Where(x => x.OccurredAt >= periodStart && x.OccurredAt < periodEnd);
        if (normalizedWarehouseCode is not null)
        {
            transactionsQuery = transactionsQuery.Where(x =>
                (x.LocationId != null && scopedLocationIds.Contains(x.LocationId.Value)) ||
                (x.Type == InventoryTransactionType.Move &&
                    ((x.SourceLocationId != null && scopedLocationIds.Contains(x.SourceLocationId.Value)) ||
                     (x.DestinationLocationId != null && scopedLocationIds.Contains(x.DestinationLocationId.Value)))));
        }
        var transactionTotals = await transactionsQuery
            .GroupBy(x => x.Type)
            .Select(group => new { Type = group.Key, Quantity = group.Sum(x => x.Quantity) })
            .ToListAsync(cancellationToken);
        IReadOnlyCollection<TransactionStatisticsFact> transactions = transactionTotals
            .Select(x => new TransactionStatisticsFact(x.Type, x.Quantity, periodStart))
            .ToArray();

        var taskRows = await db.Tasks.AsNoTracking().Where(x => x.UpdatedAt >= periodStart && x.UpdatedAt < periodEnd).Select(x => new { x.Id, x.State, x.UpdatedAt, x.DispatchContextJson }).ToListAsync(cancellationToken);
        var candidateKeys = taskRows.SelectMany(x => TaskContextLocationKeys(x.DispatchContextJson)).ToArray();
        var candidateCodes = candidateKeys.Where(x => !Guid.TryParse(x, out _)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var candidateIds = candidateKeys.Select(x => Guid.TryParse(x, out var id) ? id : (Guid?)null).Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray();
        var locationWarehouseRows = candidateKeys.Length == 0
            ? []
            : await locationRowsQuery.Where(x => candidateCodes.Contains(x.Code) || candidateIds.Contains(x.Id)).Select(x => new { x.Id, x.Code, x.WarehouseCode }).ToListAsync(cancellationToken);
        var locationWarehouse = LocationWarehouseMap(locationWarehouseRows.Select(x => (x.Id, x.Code, x.WarehouseCode)));
        var tasks = taskRows.Select(x => TaskFact(x.Id, x.State, x.UpdatedAt, x.DispatchContextJson, locationWarehouse)).ToArray();
        return StatisticsAggregation.Build(period, periodStart, periodEnd, sourceVersion, balances, transactions, tasks, locations, normalizedWarehouseCode);
    }

    private static TaskStatisticsFact TaskFact(Guid id, TaskState state, DateTimeOffset updatedAt, string? context, Dictionary<string, string?> locations)
    {
        if (string.IsNullOrWhiteSpace(context)) return new TaskStatisticsFact(id, state, updatedAt);
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(context);
            var root = json.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return new TaskStatisticsFact(id, state, updatedAt);
            var fields = new[] { "SourceLocation", "sourceLocation", "DestinationLocation", "destinationLocation" }
                .Where(k => root.TryGetProperty(k, out _))
                .Select(k => root.GetProperty(k))
                .Where(v => v.ValueKind != System.Text.Json.JsonValueKind.Null)
                .ToArray();
            if (fields.Length == 0 || fields.Any(v => v.ValueKind != System.Text.Json.JsonValueKind.String || string.IsNullOrWhiteSpace(v.GetString()))) return new TaskStatisticsFact(id, state, updatedAt);
            var codes = fields.Select(v => v.GetString()!).ToArray();
            if (codes.Any(c => !locations.TryGetValue(c, out var warehouse) || warehouse is null)) return new TaskStatisticsFact(id, state, updatedAt);
            var warehouses = codes.Select(c => locations[c]!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return warehouses.Length == 1 ? new TaskStatisticsFact(id, state, updatedAt, warehouses[0], true) : new TaskStatisticsFact(id, state, updatedAt);
        }
        catch (System.Text.Json.JsonException) { return new TaskStatisticsFact(id, state, updatedAt); }
        catch (InvalidOperationException) { return new TaskStatisticsFact(id, state, updatedAt); }
    }

    private static string[] TaskContextLocationKeys(string? context)
    {
        if (string.IsNullOrWhiteSpace(context)) return [];
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(context);
            if (json.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return [];
            return new[] { "SourceLocation", "sourceLocation", "DestinationLocation", "destinationLocation" }
                .Where(key => json.RootElement.TryGetProperty(key, out _))
                .Select(key => json.RootElement.GetProperty(key))
                .Where(value => value.ValueKind == System.Text.Json.JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                .Select(value => value.GetString()!)
                .ToArray();
        }
        catch (System.Text.Json.JsonException) { return []; }
        catch (InvalidOperationException) { return []; }
    }

    private static Dictionary<string, string?> LocationWarehouseMap(IEnumerable<(Guid Id, string Code, string WarehouseCode)> locations)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var location in locations)
        {
            Add(location.Code, location.WarehouseCode);
            Add(location.Id.ToString("D"), location.WarehouseCode);
        }
        return map;

        void Add(string key, string warehouse)
        {
            if (map.TryGetValue(key, out var current) && !string.Equals(current, warehouse, StringComparison.OrdinalIgnoreCase)) map[key] = null;
            else map.TryAdd(key, warehouse);
        }
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
