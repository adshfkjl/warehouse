using Warehouse.Wms.Api.Controllers;
using Warehouse.Wms.Infrastructure.Health;

namespace Warehouse.Wms.IntegrationTests.Reports;

public sealed class ReportsApiTests
{
    [Fact]
    public async Task Health_endpoint_is_available_without_database_or_plc()
    {
        var controller = new ReportsController(new InMemoryReportsReadModel(), new WarehouseHealthCheckService());

        var response = await controller.Health();

        var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(response.Result);
        var payload = Assert.IsType<HealthReportResponse>(ok.Value);
        Assert.Contains(payload.Components, item => item.Component == "api");
        Assert.Contains(payload.Components, item => item.Component == "database");
        Assert.Contains(payload.Components, item => item.Component == "worker");
        Assert.Contains(payload.Components, item => item.Component == "device-gateway");
        Assert.Contains(payload.Components, item => item.Component == "outbox-inbox");
    }

    [Fact]
    public async Task Health_endpoint_marks_database_probe_failure_as_unhealthy()
    {
        var controller = new ReportsController(
            new InMemoryReportsReadModel(),
            new WarehouseHealthCheckService(databaseProbe: _ => Task.FromResult(false)));

        var response = await controller.Health();

        var objectResult = Assert.IsType<Microsoft.AspNetCore.Mvc.ObjectResult>(response.Result);
        Assert.Equal(503, objectResult.StatusCode);
        var payload = Assert.IsType<HealthReportResponse>(objectResult.Value);
        Assert.Equal("Unhealthy", payload.OverallStatus);
    }
}
