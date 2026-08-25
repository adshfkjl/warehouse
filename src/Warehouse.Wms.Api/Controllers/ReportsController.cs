using Microsoft.AspNetCore.Mvc;
using Warehouse.Wms.Infrastructure.Health;

namespace Warehouse.Wms.Api.Controllers;

[ApiController]
[Route("api/reports")]
public sealed class ReportsController : ControllerBase
{
    private readonly IReportsReadModel _reports;
    private readonly WarehouseHealthCheckService _health;

    public ReportsController(IReportsReadModel reports, WarehouseHealthCheckService health)
    {
        _reports = reports ?? throw new ArgumentNullException(nameof(reports));
        _health = health ?? throw new ArgumentNullException(nameof(health));
    }

    [HttpGet("inventory")]
    public Task<ActionResult<IReadOnlyCollection<InventoryReportRow>>> Inventory()
        => Task.FromResult<ActionResult<IReadOnlyCollection<InventoryReportRow>>>(Ok(_reports.Inventory));

    [HttpGet("locations/utilization")]
    public Task<ActionResult<IReadOnlyCollection<LocationUtilizationReport>>> LocationUtilization()
    {
        var result = _reports.LocationUtilization
            .Select(item => new LocationUtilizationReport(
                item.LocationCode,
                item.Capacity,
                item.OccupiedUnits,
                item.OccupiedWeightKg,
                item.Capacity <= 0 ? 0m : Math.Round(item.OccupiedUnits * 100m / item.Capacity, 2)))
            .ToArray();
        return Task.FromResult<ActionResult<IReadOnlyCollection<LocationUtilizationReport>>>(Ok(result));
    }

    [HttpGet("inbound-outbound")]
    public Task<ActionResult<IReadOnlyCollection<InboundOutboundReportRow>>> InboundOutbound()
        => Task.FromResult<ActionResult<IReadOnlyCollection<InboundOutboundReportRow>>>(Ok(_reports.InboundOutbound));

    [HttpGet("transfers")]
    public Task<ActionResult<IReadOnlyCollection<TransferReportRow>>> Transfers()
        => Task.FromResult<ActionResult<IReadOnlyCollection<TransferReportRow>>>(Ok(_reports.Transfers));

    [HttpGet("pallets/{palletCode}")]
    public Task<ActionResult<IReadOnlyCollection<PalletTraceReportRow>>> PalletTrace(string palletCode)
    {
        if (string.IsNullOrWhiteSpace(palletCode))
        {
            return Task.FromResult<ActionResult<IReadOnlyCollection<PalletTraceReportRow>>>(BadRequest("palletCode is required"));
        }

        var result = _reports.PalletTraces
            .Where(item => string.Equals(item.PalletCode, palletCode.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return Task.FromResult<ActionResult<IReadOnlyCollection<PalletTraceReportRow>>>(Ok(result));
    }

    [HttpGet("stocktaking-differences")]
    public Task<ActionResult<IReadOnlyCollection<StocktakingDifferenceReportRow>>> StocktakingDifferences()
        => Task.FromResult<ActionResult<IReadOnlyCollection<StocktakingDifferenceReportRow>>>(Ok(_reports.StocktakingDifferences));

    [HttpGet("device-alarms")]
    public Task<ActionResult<IReadOnlyCollection<DeviceAlarmReportRow>>> DeviceAlarms()
        => Task.FromResult<ActionResult<IReadOnlyCollection<DeviceAlarmReportRow>>>(Ok(_reports.DeviceAlarms));

    [HttpGet("health")]
    public async Task<ActionResult<HealthReportResponse>> Health(CancellationToken cancellationToken = default)
    {
        var report = await _health.GetReportAsync(cancellationToken);
        return report.OverallStatus == "Unhealthy"
            ? StatusCode(StatusCodes.Status503ServiceUnavailable, report)
            : Ok(report);
    }
}
