using Microsoft.EntityFrameworkCore;
using Warehouse.Wms.Application.Reports;
using Warehouse.Wms.Domain.Inventory;
using Warehouse.Wms.Domain.Tasks;
using Warehouse.Wms.Infrastructure.Persistence;

namespace Warehouse.Wms.Infrastructure.Reports;

public sealed record InventoryStatisticsFact(decimal Quantity, decimal WeightKg, InventoryStatus Status, Guid? LocationId);
public sealed record TransactionStatisticsFact(InventoryTransactionType Type, decimal Quantity, DateTimeOffset OccurredAt);
public sealed record TaskStatisticsFact(TaskState State, DateTimeOffset UpdatedAt);
public sealed record LocationStatisticsFact(int Capacity, bool Occupied);

public sealed class SqlServerStatisticsSource(IDbContextFactory<WarehouseDbContext> factory) : IStatisticsSource
{
    public async Task<StatisticsBatchRequest?> BuildAsync(StatisticsPeriod period, DateTimeOffset periodStart, DateTimeOffset periodEnd, string sourceVersion, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var balances = await db.InventoryBalances.AsNoTracking().Select(x => new InventoryStatisticsFact(x.Quantity, x.WeightKg, x.Status, x.LocationId)).ToListAsync(cancellationToken);
        var transactions = await db.InventoryTransactions.AsNoTracking().Where(x => x.OccurredAt >= periodStart && x.OccurredAt < periodEnd).Select(x => new TransactionStatisticsFact(x.Type, x.Quantity, x.OccurredAt)).ToListAsync(cancellationToken);
        var tasks = await db.TaskStateHistories.AsNoTracking().Where(x => x.OccurredAt >= periodStart && x.OccurredAt < periodEnd).Select(x => new TaskStatisticsFact(x.ToState, x.OccurredAt)).ToListAsync(cancellationToken);
        var locationRows = await db.Locations.AsNoTracking().Select(x => new { x.Id, x.Capacity }).ToListAsync(cancellationToken);
        var locations = locationRows.Select(x => new LocationStatisticsFact(x.Capacity, balances.Any(b => b.LocationId == x.Id && b.Quantity > 0))).ToArray();
        return StatisticsAggregation.Build(period, periodStart, periodEnd, sourceVersion, balances, transactions, tasks, locations);
    }
}

public static class StatisticsAggregation
{
    public static StatisticsBatchRequest? Build(StatisticsPeriod period, DateTimeOffset start, DateTimeOffset end, string sourceVersion, IReadOnlyCollection<InventoryStatisticsFact> balances, IReadOnlyCollection<TransactionStatisticsFact> transactions, IReadOnlyCollection<TaskStatisticsFact> tasks, IReadOnlyCollection<LocationStatisticsFact>? locations = null)
    {
        if (balances.Count == 0 && transactions.Count == 0 && tasks.Count == 0) return null;
        var inbound = transactions.Where(x => x.Type == InventoryTransactionType.Increase).Sum(x => x.Quantity);
        var outbound = transactions.Where(x => x.Type == InventoryTransactionType.Decrease).Sum(x => x.Quantity);
        var transfer = transactions.Where(x => x.Type == InventoryTransactionType.Move).Sum(x => x.Quantity);
        var completed = tasks.Count(x => x.State == TaskState.Succeeded);
        var successRate = tasks.Count == 0 ? 0m : completed * 100m / tasks.Count;
        var utilization = locations is null || locations.Count == 0 ? 0m : locations.Sum(x => x.Capacity) == 0 ? 0m : locations.Count(x => x.Occupied) * 100m / locations.Sum(x => x.Capacity);
        var kpi = new StatisticsKpi(balances.Sum(x => x.Quantity), balances.Sum(x => x.WeightKg), utilization, inbound, outbound, transfer, successRate, tasks.Count(x => x.State is TaskState.Failed or TaskState.TimedOut or TaskState.PhysicalStateUnknown), 0);
        var trend = new StatisticsTrendPoint(start, inbound, outbound, transfer, kpi.ExceptionCount);
        var states = tasks.GroupBy(x => x.State.ToString()).Select(x => new TaskStateCount(x.Key, x.Count())).ToArray();
        return new StatisticsBatchRequest(period, start, end, sourceVersion, Kpi: kpi, Trends: [trend], TaskStates: states);
    }
}
