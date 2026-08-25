using Microsoft.AspNetCore.Mvc;
using Warehouse.Wms.Application.Integrations;

namespace Warehouse.Wms.Api.Controllers;

[ApiController]
[Route("api/integrations/v1")]
public sealed class IntegrationsController(IIntegrationCommandService service) : ControllerBase
{
    [HttpPost("inbound-notices")]
    public Task<IActionResult> InboundNotice(ExternalIntegrationRequest request, CancellationToken cancellationToken)
        => Enqueue(IntegrationMessageType.InboundNotice, request, cancellationToken);

    [HttpPost("outbound-requests")]
    public Task<IActionResult> OutboundRequest(ExternalIntegrationRequest request, CancellationToken cancellationToken)
        => Enqueue(IntegrationMessageType.OutboundRequest, request, cancellationToken);

    [HttpPost("cancel-requests")]
    public Task<IActionResult> CancelRequest(ExternalIntegrationRequest request, CancellationToken cancellationToken)
        => Enqueue(IntegrationMessageType.CancelRequest, request, cancellationToken);

    [HttpPost("status-queries")]
    public Task<IActionResult> StatusQuery(ExternalIntegrationRequest request, CancellationToken cancellationToken)
        => Enqueue(IntegrationMessageType.StatusQuery, request, cancellationToken);

    [HttpPost("result-callbacks")]
    public Task<IActionResult> ResultCallback(ExternalIntegrationRequest request, CancellationToken cancellationToken)
        => Enqueue(IntegrationMessageType.ResultCallback, request, cancellationToken);

    [HttpPost("inventory-sync")]
    public Task<IActionResult> InventorySync(ExternalIntegrationRequest request, CancellationToken cancellationToken)
        => Enqueue(IntegrationMessageType.InventorySync, request, cancellationToken);

    private async Task<IActionResult> Enqueue(IntegrationMessageType type, ExternalIntegrationRequest request, CancellationToken cancellationToken)
    {
        var result = await service.EnqueueAsync(type, request, cancellationToken);
        return result.Status == "Disabled"
            ? NotFound(new { code = "INTEGRATION_DISABLED", result.IdempotencyKey })
            : Accepted(new { result.Status, result.Duplicate, result.IdempotencyKey });
    }
}
