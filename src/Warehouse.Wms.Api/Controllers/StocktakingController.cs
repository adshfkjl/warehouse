using Microsoft.AspNetCore.Mvc;
using Warehouse.Wms.Application.Stocktaking;

namespace Warehouse.Wms.Api.Controllers;

[ApiController]
[Route("api/stocktaking")]
public sealed class StocktakingController : ControllerBase
{
    private readonly StocktakingService _service;

    public StocktakingController(StocktakingService service)
        => _service = service ?? throw new ArgumentNullException(nameof(service));

    [HttpPost]
    public ActionResult<object> Create([FromBody] StocktakingRequest request)
    {
        try { return Ok(_service.Create(request)); }
        catch (ArgumentException exception) { return BadRequest(new { code = "INVALID_STOCKTAKING", message = exception.Message }); }
        catch (InvalidOperationException exception) { return Conflict(new { code = "STOCKTAKING_REJECTED", message = exception.Message }); }
    }

    [HttpPost("{taskNumber}/start")]
    public ActionResult<object> Start(string taskNumber)
    {
        try { return Ok(_service.Start(taskNumber)); }
        catch (KeyNotFoundException exception) { return NotFound(new { code = "STOCKTAKING_NOT_FOUND", message = exception.Message }); }
        catch (InvalidOperationException exception) { return Conflict(new { code = "STOCKTAKING_REJECTED", message = exception.Message }); }
    }
}
