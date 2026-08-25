using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Warehouse.Wms.Api.Controllers;
using Warehouse.Wms.Application.Integrations;
using Warehouse.Wms.Infrastructure.Integrations;

namespace Warehouse.Wms.IntegrationTests.Integrations;

public sealed class IntegrationApiTests
{
    [Fact]
    public async Task Disabled_adapter_returns_not_found_without_affecting_controller_host()
    {
        var controller = new IntegrationsController(new IntegrationCommandService(new InMemoryIntegrationOutbox()));
        using var document = JsonDocument.Parse("{\"sku\":\"MAT-01\"}");
        var result = await controller.InboundNotice(new ExternalIntegrationRequest("ERP", "v1", "in-001", null, document.RootElement.Clone()), CancellationToken.None);

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        Assert.Equal("INTEGRATION_DISABLED", notFound.Value?.GetType().GetProperty("code")?.GetValue(notFound.Value));
    }

    [Fact]
    public async Task Versioned_result_callback_is_accepted_and_replay_is_explicit()
    {
        var outbox = new InMemoryIntegrationOutbox();
        var controller = new IntegrationsController(new IntegrationCommandService(outbox, enabled: true));
        using var document = JsonDocument.Parse("{\"taskNumber\":\"T-01\",\"status\":\"Succeeded\"}");
        var request = new ExternalIntegrationRequest("WCS", "v1", "result-001", "T-01", document.RootElement.Clone());

        var first = Assert.IsType<AcceptedResult>(await controller.ResultCallback(request, CancellationToken.None));
        var replay = Assert.IsType<AcceptedResult>(await controller.ResultCallback(request, CancellationToken.None));

        Assert.Contains("Queued", first.Value?.ToString());
        Assert.Contains("AlreadyQueued", replay.Value?.ToString());
        Assert.Single(outbox.Snapshot());
    }
}
