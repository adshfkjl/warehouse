using Microsoft.AspNetCore.Mvc;
using Warehouse.Wms.Application.Relocation;

namespace Warehouse.Wms.Api.Controllers;

[ApiController]
[Route("api/relocations")]
public sealed class RelocationController : ControllerBase
{
    private readonly RelocationService _service;

    public RelocationController(RelocationService service)
        => _service = service ?? throw new ArgumentNullException(nameof(service));

    [HttpPost]
    public async Task<ActionResult<RelocationResult>> Submit(
        [FromBody] RelocationRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await _service.SubmitAsync(request, cancellationToken));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { code = "INVALID_RELOCATION", message = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(new { code = "RELOCATION_REJECTED", message = exception.Message });
        }
    }
}
