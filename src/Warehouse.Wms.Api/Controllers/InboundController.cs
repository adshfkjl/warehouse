using Microsoft.AspNetCore.Mvc;
using Warehouse.Wms.Application.Inbound;

namespace Warehouse.Wms.Api.Controllers;

public sealed record PutawayTaskCommand(
    string TaskNumber,
    string DeviceId,
    Guid? LoadingPointId = null,
    string ProtocolVersion = "v1",
    Guid? RequestedLocationId = null,
    decimal LengthMm = 1m,
    decimal WidthMm = 1m,
    decimal HeightMm = 1m,
    string? IdempotencyKey = null,
    int Priority = 0,
    int MaxAttempts = 1);

[ApiController]
[Route("api/inbound")]
public sealed class InboundController : ControllerBase
{
    private readonly PutawayTaskService _putawayTasks;

    public InboundController(PutawayTaskService putawayTasks)
    {
        _putawayTasks = putawayTasks ?? throw new ArgumentNullException(nameof(putawayTasks));
    }

    [HttpPost("pending/{pendingInboundInventoryId:guid}/putaway")]
    public async Task<ActionResult<PutawayTaskResult>> SubmitPutaway(
        Guid pendingInboundInventoryId,
        [FromBody] PutawayTaskCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        try
        {
            var result = await _putawayTasks.SubmitAsync(
                pendingInboundInventoryId,
                new PutawayTaskRequest(
                    command.TaskNumber,
                    command.DeviceId,
                    command.LoadingPointId,
                    command.ProtocolVersion,
                    command.RequestedLocationId,
                    command.LengthMm,
                    command.WidthMm,
                    command.HeightMm,
                    command.IdempotencyKey,
                    command.Priority,
                    command.MaxAttempts),
                cancellationToken);
            return Ok(result);
        }
        catch (KeyNotFoundException exception)
        {
            return NotFound(new { code = "PENDING_INBOUND_NOT_FOUND", message = exception.Message });
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { code = "INVALID_PUTAWAY_REQUEST", message = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(new { code = "PUTAWAY_REJECTED", message = exception.Message });
        }
    }
}
