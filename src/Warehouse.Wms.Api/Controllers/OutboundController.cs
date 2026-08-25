using Microsoft.AspNetCore.Mvc;
using Warehouse.Wms.Application.Outbound;

namespace Warehouse.Wms.Api.Controllers;

[ApiController]
[Route("api/outbound")]
public sealed class OutboundController : ControllerBase
{
    private readonly OutboundReviewService _review;
    public OutboundController(OutboundReviewService review) => _review = review ?? throw new ArgumentNullException(nameof(review));

    [HttpPost("{taskNumber}/review")]
    public async Task<ActionResult<OutboundReviewResult>> Review(string taskNumber, [FromBody] OutboundReviewRequest request, CancellationToken cancellationToken)
    {
        try { return Ok(await _review.ReviewAsync(taskNumber, request, cancellationToken)); }
        catch (KeyNotFoundException exception) { return NotFound(new { code = "OUTBOUND_TASK_NOT_FOUND", message = exception.Message }); }
        catch (InvalidOperationException exception) { return Conflict(new { code = "OUTBOUND_REVIEW_REJECTED", message = exception.Message }); }
        catch (ArgumentException exception) { return BadRequest(new { code = "INVALID_OUTBOUND_REVIEW", message = exception.Message }); }
    }
}
