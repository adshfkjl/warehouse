using Microsoft.AspNetCore.Mvc;
using Warehouse.Wms.Application.Points;

namespace Warehouse.Wms.Api.Controllers;

[ApiController]
[Route("api/warehouse")]
public sealed class WarehousePointsController : ControllerBase
{
    private readonly IPointReadModel _points;
    public WarehousePointsController(IPointReadModel points) => _points = points;

    [HttpGet("points")]
    public ActionResult<IReadOnlyCollection<WarehousePointSnapshot>> Points([FromQuery] PointQuery query)
        => Ok(_points.Query(query));

    [HttpGet("points/{locationCode}")]
    public ActionResult<WarehousePointSnapshot> Point(string locationCode)
    {
        if (string.IsNullOrWhiteSpace(locationCode)) return BadRequest("locationCode is required");
        var point = _points.GetByLocation(locationCode);
        return point is null ? NotFound() : Ok(point);
    }

    [HttpGet("pallets/{palletCode}/position")]
    public ActionResult<PalletPosition> PalletPosition(string palletCode)
    {
        if (string.IsNullOrWhiteSpace(palletCode)) return BadRequest("palletCode is required");
        var position = _points.FindPallet(palletCode);
        return position is null ? NotFound() : Ok(position);
    }
}
