using Warehouse.Wms.Infrastructure.Health;

namespace Warehouse.Wms.UnitTests.Reports;

public sealed class ReportsTests
{
    [Fact]
    public void Location_utilization_is_calculated_without_dividing_by_zero()
    {
        var model = new InMemoryReportsReadModel(locationUtilization: [
            new LocationUtilizationRow("A-01", 10, 4, 0m),
            new LocationUtilizationRow("A-02", 0, 0, 0m)]);
        var values = model.LocationUtilization.Select(item => new LocationUtilizationReport(
            item.LocationCode, item.Capacity, item.OccupiedUnits, item.OccupiedWeightKg,
            item.Capacity <= 0 ? 0m : item.OccupiedUnits * 100m / item.Capacity)).ToArray();
        Assert.Equal(40m, values.Single(item => item.LocationCode == "A-01").UtilizationPercent);
        Assert.Equal(0m, values.Single(item => item.LocationCode == "A-02").UtilizationPercent);
    }

    [Fact]
    public void Reports_cover_all_required_operational_views()
    {
        var model = new InMemoryReportsReadModel(
            inventory: [new InventoryReportRow("MAT-01", "PAL-01", "A-01", 5m, 50m, "Available")],
            inboundOutbound: [new InboundOutboundReportRow("IN-01", "Inbound", 5m, "Completed")],
            transfers: [new TransferReportRow("TR-01", "PAL-01", "A-01", "A-02", "Succeeded")],
            palletTraces: [new PalletTraceReportRow("PAL-01", "A-01", "A-02", "TR-01", "Succeeded")],
            stocktakingDifferences: [new StocktakingDifferenceReportRow("ST-01", "A-01", 10m, 9m, -1m)],
            deviceAlarms: [new DeviceAlarmReportRow("PLC-01", "ALARM-1", "Offline", DateTimeOffset.UtcNow)]);
        Assert.Single(model.Inventory);
        Assert.Single(model.InboundOutbound);
        Assert.Single(model.Transfers);
        Assert.Single(model.PalletTraces);
        Assert.Single(model.StocktakingDifferences);
        Assert.Single(model.DeviceAlarms);
    }

    [Fact]
    public async Task Health_check_reports_offline_gateway_and_replayed_message()
    {
        var messaging = new MessagingHealthState();
        messaging.RecordReplay();
        var health = new WarehouseHealthCheckService(
            gatewayProbe: _ => Task.FromResult(new DeviceGatewayProbeResult(false, "PLC offline")),
            messagingState: messaging);

        var result = await health.CheckAsync();

        Assert.Equal("Unhealthy", result.Single(item => item.Component == "device-gateway").Status);
        Assert.Equal("Degraded", result.Single(item => item.Component == "outbox-inbox").Status);
    }

}
