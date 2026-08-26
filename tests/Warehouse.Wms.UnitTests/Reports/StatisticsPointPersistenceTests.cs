using Warehouse.Wms.Application.Points;
using Warehouse.Wms.Application.Reports;
using Warehouse.Wms.Infrastructure.Reports;
using Warehouse.Wms.Infrastructure.Warehouse;
using Warehouse.Wms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

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

    private sealed class TestDbContextFactory : IDbContextFactory<WarehouseDbContext>
    {
        private readonly bool _unused = true;
        public WarehouseDbContext CreateDbContext() => throw new InvalidOperationException("test double");
        public ValueTask<WarehouseDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) { _ = _unused; _ = cancellationToken; throw new InvalidOperationException("test double"); }
    }
}
