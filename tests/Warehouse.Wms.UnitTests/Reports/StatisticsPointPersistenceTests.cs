using Warehouse.Wms.Application.Points;
using Warehouse.Wms.Application.Reports;
using Warehouse.Wms.Infrastructure.Reports;
using Warehouse.Wms.Infrastructure.Warehouse;
using Warehouse.Wms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Warehouse.Wms.Domain.Inventory;
using Warehouse.Wms.Domain.Tasks;

namespace Warehouse.Wms.UnitTests.Reports;

public sealed class StatisticsPointPersistenceTests
{
    [Fact(Skip = "Requires SQL Server integration fixture")]
    public async Task Sql_statistics_service_replays_same_batch_and_rejects_source_conflict()
    {
        var service = new SqlServerStatisticsService(new TestDbContextFactory());
        var start = new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero);

        var first = await service.GenerateAsync(StatisticsPeriod.Day, start, start.AddDays(1), "v1");
        var replay = await service.GenerateAsync(StatisticsPeriod.Day, start, start.AddDays(1), "v1");

        Assert.Equal(first.BatchId, replay.BatchId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GenerateAsync(StatisticsPeriod.Day, start, start.AddDays(1), "v2"));
    }

    [Fact(Skip = "Requires SQL Server integration fixture")]
    public async Task Sql_point_model_keeps_highest_source_version_and_marks_physical_unknown()
    {
        var model = new SqlServerPointReadModel(new TestDbContextFactory(), TimeSpan.FromMinutes(1));
        var now = DateTimeOffset.UtcNow;
        await model.UpsertAsync(new WarehousePointSnapshot("WH", "Z", "A", "R", 1, "L-1", "Occupied", "P", "M", null, null, 1, 1, now, 1, false, null, null, null));
        await model.UpsertAsync(new WarehousePointSnapshot("WH", "Z", "A", "R", 1, "L-1", "PhysicalUnknown", "P", "M", null, null, 1, 1, now.AddMinutes(-2), 2, true, "unknown", "Unknown", null));

        var point = Assert.Single(await model.QueryAsync(new PointQuery(), now));
        Assert.Equal("PhysicalUnknown", point.Status);
        Assert.Equal(2, point.SourceVersion);
        Assert.Equal("Stale", point.Freshness);
    }

    [Fact]
    public async Task Statistics_worker_honors_cancellation_before_generation()
    {
        var worker = new StatisticsWorker(new InMemoryStatisticsService(), new StatisticsScheduleOptions(StatisticsPeriod.Hour, TimeSpan.Zero), "v1");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.RunOnceAsync(DateTimeOffset.UtcNow, cts.Token));
    }

    [Fact]
    public async Task Statistics_worker_does_not_generate_an_incomplete_period_or_before_run_at()
    {
        var service = new InMemoryStatisticsService();
        var source = new TestStatisticsSource(new StatisticsKpi(1, 2, 3, 4, 5, 6, 7, 8, 9));
        var worker = new StatisticsWorker(service, new StatisticsScheduleOptions(StatisticsPeriod.Hour, TimeSpan.FromHours(1)), "v1", source);

        Assert.Null(await worker.RunOnceAsync(new DateTimeOffset(2026, 8, 26, 0, 30, 0, TimeSpan.Zero)));
        var snapshot = await worker.RunOnceAsync(new DateTimeOffset(2026, 8, 26, 1, 30, 0, TimeSpan.Zero));
        Assert.NotNull(snapshot);
        Assert.Equal(new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero), snapshot!.PeriodStart);
        Assert.Equal(1, snapshot.Kpi.InventoryQuantity);
    }

    [Fact]
    public async Task Statistics_worker_does_not_write_when_source_unavailable()
    {
        var service = new InMemoryStatisticsService();
        var worker = new StatisticsWorker(service, new StatisticsScheduleOptions(StatisticsPeriod.Hour, TimeSpan.Zero), "v1", new TestStatisticsSource(null));
        Assert.Null(await worker.RunOnceAsync(new DateTimeOffset(2026, 8, 26, 2, 0, 0, TimeSpan.Zero)));
        Assert.Null(service.LatestSuccessful);
    }

    [Fact]
    public async Task Sql_point_query_honors_cancellation_before_opening_context()
    {
        var model = new SqlServerPointReadModel(new CanceledFactory());
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => model.QueryAsync(cancellationToken: cts.Token));
    }

    [Fact]
    public void Sql_statistics_aggregation_uses_fact_tables_and_does_not_claim_success_when_empty()
    {
        var start = new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero);
        var request = StatisticsAggregation.Build(StatisticsPeriod.Day, start, start.AddDays(1), "v1",
            [new InventoryStatisticsFact(12, 24, InventoryStatus.Available, Guid.NewGuid())],
            [new TransactionStatisticsFact(InventoryTransactionType.Increase, 5, start.AddHours(1)), new TransactionStatisticsFact(InventoryTransactionType.Decrease, 2, start.AddHours(2))],
            [new TaskStatisticsFact(Guid.NewGuid(), TaskState.Succeeded, start.AddHours(1)), new TaskStatisticsFact(Guid.NewGuid(), TaskState.Failed, start.AddHours(2))],
            [new LocationStatisticsFact(10, 1), new LocationStatisticsFact(10, 0)]);
        Assert.NotNull(request);
        Assert.Equal(12, request!.Kpi!.InventoryQuantity);
        Assert.Equal(5, request.Kpi.InboundQuantity);
        Assert.Equal(50, request.Kpi.TaskSuccessRatePercent);
        Assert.Equal(5, request.Kpi.LocationUtilizationPercent);
        Assert.Single(request.Trends!);
        Assert.Contains(request.TaskStates!, x => x.State == nameof(TaskState.Failed) && x.Count == 1);
        var taskId = Guid.NewGuid();
        var deduped = StatisticsAggregation.Build(StatisticsPeriod.Day, start, start.AddDays(1), "v1", [new InventoryStatisticsFact(1, 1, InventoryStatus.Available, null)], [], [new TaskStatisticsFact(taskId, TaskState.Executing, start.AddHours(1)), new TaskStatisticsFact(taskId, TaskState.Succeeded, start.AddHours(2))], [new LocationStatisticsFact(10, 1), new LocationStatisticsFact(10, 0)]);
        Assert.Equal(100, deduped!.Kpi!.TaskSuccessRatePercent);
        Assert.Null(StatisticsAggregation.Build(StatisticsPeriod.Day, start, start.AddDays(1), "v1", [], [], []));
    }

    [Fact]
    public async Task Statistics_worker_propagates_configured_warehouse_scope()
    {
        var service = new InMemoryStatisticsService();
        var source = new TestStatisticsSource(new StatisticsKpi(1, 1, 1, 1, 1, 1, 100, 0, 0));
        var worker = new StatisticsWorker(service, new StatisticsScheduleOptions(StatisticsPeriod.Hour, TimeSpan.Zero, true, "WH-01"), "v1", source);
        var snapshot = await worker.RunOnceAsync(new DateTimeOffset(2026, 8, 26, 1, 0, 0, TimeSpan.Zero));
        Assert.Equal("WH-01", snapshot!.WarehouseCode);
    }

    private sealed class TestStatisticsSource(StatisticsKpi? kpi) : IStatisticsSource
    {
        public Task<StatisticsBatchRequest?> BuildAsync(StatisticsPeriod period, DateTimeOffset start, DateTimeOffset end, string sourceVersion, CancellationToken cancellationToken = default)
            => Task.FromResult<StatisticsBatchRequest?>(kpi is null ? null : new StatisticsBatchRequest(period, start, end, sourceVersion, Kpi: kpi));
    }

    private sealed class CanceledFactory : IDbContextFactory<WarehouseDbContext>
    {
        public WarehouseDbContext CreateDbContext() => throw new InvalidOperationException();
        public ValueTask<WarehouseDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) { _ = this; cancellationToken.ThrowIfCancellationRequested(); throw new InvalidOperationException(); }
    }

    private sealed class TestDbContextFactory : IDbContextFactory<WarehouseDbContext>
    {
        private readonly bool _unused = true;
        public WarehouseDbContext CreateDbContext() => throw new InvalidOperationException("test double");
        public ValueTask<WarehouseDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) { _ = _unused; _ = cancellationToken; throw new InvalidOperationException("test double"); }
    }
}
