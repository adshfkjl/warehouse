using Microsoft.AspNetCore.Mvc;
using Warehouse.Wms.Application.Exceptions;

namespace Warehouse.Wms.Api.Controllers;

[ApiController]
[Route("api/exceptions")]
public sealed class ExceptionsController : ControllerBase
{
    private readonly ExceptionWorkItemService _service;

    public ExceptionsController(ExceptionWorkItemService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
    }

    [HttpGet]
    public ActionResult<IReadOnlyCollection<object>> ListActive()
        => Ok(_service.ActiveWorkItems.Cast<object>().ToArray());

    [HttpPost]
    public ActionResult<object> Create([FromBody] ExceptionWorkItemRequest request)
    {
        try
        {
            return Ok(_service.CreateOrMerge(request));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { code = "INVALID_EXCEPTION", message = exception.Message });
        }
    }

    [HttpPost("{id:guid}/actions")]
    public async Task<ActionResult<object>> Execute(
        Guid id,
        [FromBody] ExceptionActionRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _service.ExecuteAsync(id, request, cancellationToken));
        }
        catch (KeyNotFoundException exception)
        {
            return NotFound(new { code = "EXCEPTION_NOT_FOUND", message = exception.Message });
        }
        catch (UnauthorizedAccessException exception)
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                new { code = "AUTHORIZATION_DENIED", message = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(new { code = "EXCEPTION_ACTION_REJECTED", message = exception.Message });
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { code = "INVALID_EXCEPTION_ACTION", message = exception.Message });
        }
    }
}
