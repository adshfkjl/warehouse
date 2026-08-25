using Microsoft.AspNetCore.Mvc;
using Warehouse.Wms.Application.Reports;

namespace Warehouse.Wms.Api.Controllers;

[ApiController]
[Route("api/reports")]
public sealed class StatisticsController : ControllerBase
{
    private readonly IStatisticsService _statistics;
    public StatisticsController(IStatisticsService statistics) => _statistics = statistics;

    [HttpGet("summary")]
    public ActionResult<StatisticsSnapshot> Summary([FromQuery] StatisticsPeriod? period = null, [FromQuery] string? warehouseCode = null)
        => Ok(_statistics.GetSummary(period, warehouseCode));

    [HttpGet("trends")]
    public ActionResult<IReadOnlyCollection<StatisticsTrendPoint>> Trends([FromQuery] StatisticsPeriod? period = null, [FromQuery] string? warehouseCode = null)
        => Ok(_statistics.GetSummary(period, warehouseCode).Trends);
}
