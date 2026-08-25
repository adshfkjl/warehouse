using Warehouse.Wms.Application.Reports;
using Warehouse.Wms.Application.Points;
using Warehouse.Wms.Infrastructure.Reports;
using Warehouse.Wms.Infrastructure.Warehouse;

namespace Warehouse.Wms.UnitTests.Reports;

public sealed class StatisticsPointTests
{
    [Fact]
    public async Task Repeating_the_same_period_is_idempotent_and_keeps_latest_success()
    {
        var service = new InMemoryStatisticsService();
        var start = new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero);
        var end = start.AddDays(1);

        var first = await service.GenerateAsync(StatisticsPeriod.Day, start, end, "v1");
        var replay = await service.GenerateAsync(StatisticsPeriod.Day, start, end, "v1");

        Assert.Equal(first.BatchId, replay.BatchId);
        Assert.Equal(first.GeneratedAt, replay.GeneratedAt);
        Assert.Equal("v1", replay.SourceVersion);
        Assert.Equal(first.BatchId, first.IdempotencyKey);
    }

    [Fact]
    public async Task Same_period_with_a_different_source_version_is_rejected()
    {
        var service = new InMemoryStatisticsService();
        var start = new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero);
        var end = start.AddDays(1);

        await service.GenerateAsync(StatisticsPeriod.Day, start, end, "v1");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GenerateAsync(StatisticsPeriod.Day, start, end, "v2"));
    }

    [Fact]
    public async Task Scheduler_generates_the_configured_period_once_for_a_due_window()
    {
        var service = new InMemoryStatisticsService();
        var scheduler = new InMemoryStatisticsScheduler(service, new StatisticsScheduleOptions(StatisticsPeriod.Hour, TimeSpan.Zero));

        var result = await scheduler.RunAsync(new DateTimeOffset(2026, 8, 26, 10, 42, 0, TimeSpan.Zero), "v1");

        Assert.Equal(StatisticsPeriod.Hour, result.Period);
        Assert.Equal(new DateTimeOffset(2026, 8, 26, 10, 0, 0, TimeSpan.Zero), result.PeriodStart);
        Assert.Equal(result, await scheduler.RunAsync(new DateTimeOffset(2026, 8, 26, 10, 55, 0, TimeSpan.Zero), "v1"));
    }

    [Fact]
    public async Task Failed_generation_does_not_replace_previous_success()
    {
        var service = new InMemoryStatisticsService();
        var start = new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero);
        var end = start.AddDays(1);
        var success = await service.GenerateAsync(StatisticsPeriod.Day, start, end, "v1");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GenerateAsync(StatisticsPeriod.Day, start.AddDays(1), end.AddDays(1), "fail", fail: true));
        Assert.Same(success, service.LatestSuccessful);
    }

    [Fact]
    public async Task Summary_filters_latest_success_by_warehouse_scope()
    {
        var service = new InMemoryStatisticsService();
        var start = new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero);
        await service.GenerateAsync(new StatisticsBatchRequest(
            StatisticsPeriod.Day, start, start.AddDays(1), "v1", "WH-01",
            new StatisticsKpi(10, 100, 50, 2, 1, 0, 100, 0, 0)));
        await service.GenerateAsync(new StatisticsBatchRequest(
            StatisticsPeriod.Day, start, start.AddDays(1), "v1", "WH-02",
            new StatisticsKpi(20, 200, 60, 4, 2, 1, 100, 1, 0)));

        var summary = service.GetSummary(StatisticsPeriod.Day, "WH-01");

        Assert.Equal("WH-01", summary.WarehouseCode);
        Assert.Equal(10, summary.Kpi.InventoryQuantity);
    }

    [Fact]
    public void Point_query_filters_space_and_marks_stale_or_unknown_without_mutating_state()
    {
        var now = DateTimeOffset.UtcNow;
        var model = new InMemoryPointReadModel([
            new WarehousePointSnapshot("WH-01", "Z1", "A1", "R01", 2, "A1-01-02", "Occupied", "PAL-01", "MAT-01", "物料", "LOT1", 4, 40, now.AddSeconds(-5), 1, false, null, "Idle", null),
            new WarehousePointSnapshot("WH-01", "Z1", "A2", "R01", 2, "A2-01-02", "PhysicalUnknown", "PAL-02", "MAT-02", "物料", "LOT2", 1, 10, now.AddMinutes(-10), 2, true, "TASK-2", "Unknown", "LP-01")
        ], freshnessThreshold: TimeSpan.FromMinutes(2));

        var result = model.Query(new PointQuery { Aisle = "A2" }, now);

        var point = Assert.Single(result);
        Assert.Equal("A2-01-02", point.LocationCode);
        Assert.Equal("Stale", point.Freshness);
        Assert.Equal("PhysicalUnknown", point.Status);
        Assert.True(point.IsLocked);
    }

    [Fact]
    public void Pallet_position_lookup_returns_current_point_and_history_summary()
    {
        var model = new InMemoryPointReadModel([
            new WarehousePointSnapshot("WH-01", "Z1", "A1", "R01", 2, "A1-01-02", "Occupied", "PAL-01", "MAT-01", "物料", null, 4, 40, DateTimeOffset.UtcNow, 4, false, null, "Idle", null)
        ]);

        var position = model.FindPallet("PAL-01", DateTimeOffset.UtcNow);

        Assert.NotNull(position);
        Assert.Equal("A1-01-02", position!.CurrentLocationCode);
    }

    [Fact]
    public void Point_read_model_keeps_the_highest_observation_version_per_location()
    {
        var now = DateTimeOffset.UtcNow;
        var model = new InMemoryPointReadModel([
            new WarehousePointSnapshot("WH-01", "Z1", "A1", "R01", 1, "A1-01-01", "Occupied", "OLD", "MAT-01", null, null, 1, 1, now, 1, false, null, null, null),
            new WarehousePointSnapshot("WH-01", "Z1", "A1", "R01", 1, "A1-01-01", "Free", null, null, null, null, 0, 0, now.AddSeconds(1), 2, false, null, null, null)
        ]);

        var point = Assert.Single(model.Query());

        Assert.Equal(2, point.SourceVersion);
        Assert.Equal("Free", point.Status);
    }

    [Fact]
    public void Point_query_rejects_unknown_empty_location_without_mutating_snapshot()
    {
        var now = DateTimeOffset.UtcNow;
        var model = new InMemoryPointReadModel([
            new WarehousePointSnapshot("WH-01", "Z1", "A1", "R01", 1, "A1-01-01", "Free", null, null, null, null, 0, 0, now, 1, false, null, null, null)
        ]);

        Assert.Null(model.GetByLocation(" ", now));
        Assert.Equal("Free", Assert.Single(model.Query()).Status);
    }
}
