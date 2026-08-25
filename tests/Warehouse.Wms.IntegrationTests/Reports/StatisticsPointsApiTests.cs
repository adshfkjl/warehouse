using Microsoft.AspNetCore.Mvc;
using Warehouse.Wms.Api.Controllers;
using Warehouse.Wms.Application.Points;
using Warehouse.Wms.Application.Reports;
using Warehouse.Wms.Infrastructure.Reports;
using Warehouse.Wms.Infrastructure.Warehouse;

namespace Warehouse.Wms.IntegrationTests.Reports;

public sealed class StatisticsPointsApiTests
{
    [Fact]
    public async Task Statistics_summary_is_read_only_and_returns_freshness_metadata()
    {
        var statistics = new InMemoryStatisticsService();
        var start = new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero);
        await statistics.GenerateAsync(StatisticsPeriod.Day, start, start.AddDays(1), "test-v1");
        var controller = new StatisticsController(statistics);

        var response = controller.Summary(StatisticsPeriod.Day);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var summary = Assert.IsType<StatisticsSnapshot>(ok.Value);
        Assert.Equal("Fresh", summary.Freshness);
        Assert.Equal("test-v1", summary.SourceVersion);
    }

    [Fact]
    public void Point_detail_is_read_only_and_does_not_expose_mutation_actions()
    {
        var model = new InMemoryPointReadModel([
            new WarehousePointSnapshot("WH-01", "Z1", "A1", "R01", 1, "A1-01-01", "Locked", "PAL-01", "MAT-01", "物料", "LOT-1", 2, 20, DateTimeOffset.UtcNow, 7, true, "TASK-1", "Executing", "LP-01")
        ]);
        var controller = new WarehousePointsController(model);

        var response = controller.Point("A1-01-01");

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var point = Assert.IsType<WarehousePointSnapshot>(ok.Value);
        Assert.True(point.IsLocked);
        Assert.Equal("TASK-1", point.LockReason);
        Assert.DoesNotContain(typeof(WarehousePointsController).GetMethods(), method => method.Name.StartsWith("Post", StringComparison.OrdinalIgnoreCase));
    }
}
