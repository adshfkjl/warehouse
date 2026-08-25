using System.Text.Json;
using Warehouse.Wms.Application.Integrations;
using Warehouse.Wms.Infrastructure.Integrations;

namespace Warehouse.Wms.UnitTests.Integrations;

public sealed class IntegrationContractTests
{
    [Fact]
    public async Task Enabled_v1_message_is_queued_once_with_payload_hash()
    {
        var outbox = new InMemoryIntegrationOutbox();
        var service = new IntegrationCommandService(outbox, enabled: true);
        using var document = JsonDocument.Parse("{\"documentNumber\":\"IN-001\"}");
        var request = new ExternalIntegrationRequest("ERP", "v1", "in-001", "corr-1", document.RootElement.Clone());

        var first = await service.EnqueueAsync(IntegrationMessageType.InboundNotice, request);
        var replay = await service.EnqueueAsync(IntegrationMessageType.InboundNotice, request);

        Assert.True(first.Accepted);
        Assert.False(first.Duplicate);
        Assert.True(replay.Duplicate);
        Assert.Single(outbox.Snapshot());
        Assert.Contains("PayloadSha256", outbox.Snapshot().Single().Payload);
    }

    [Fact]
    public async Task Disabled_integration_does_not_enqueue_or_block_local_wms()
    {
        var outbox = new InMemoryIntegrationOutbox();
        var service = new IntegrationCommandService(outbox);
        using var document = JsonDocument.Parse("{}");
        var result = await service.EnqueueAsync(IntegrationMessageType.StatusQuery,
            new ExternalIntegrationRequest("MES", "v1", "status-001", null, document.RootElement.Clone()));

        Assert.Equal("Disabled", result.Status);
        Assert.Empty(outbox.Snapshot());
    }

    [Fact]
    public async Task Enabled_integration_rejects_idempotency_key_reuse_with_a_different_digest()
    {
        var outbox = new InMemoryIntegrationOutbox();
        var service = new IntegrationCommandService(outbox, enabled: true);
        using var firstPayload = JsonDocument.Parse("{\"sku\":\"MAT-01\"}");
        using var conflictingPayload = JsonDocument.Parse("{\"sku\":\"MAT-02\"}");

        await service.EnqueueAsync(
            IntegrationMessageType.InboundNotice,
            new ExternalIntegrationRequest("ERP", "v1", "in-conflict", null, firstPayload.RootElement.Clone()));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnqueueAsync(
            IntegrationMessageType.InboundNotice,
            new ExternalIntegrationRequest("ERP", "v1", "in-conflict", null, conflictingPayload.RootElement.Clone())));
    }

    [Fact]
    public async Task Unsupported_contract_version_is_rejected_before_outbox()
    {
        var service = new IntegrationCommandService(new InMemoryIntegrationOutbox(), enabled: true);
        using var document = JsonDocument.Parse("{}");
        await Assert.ThrowsAsync<ArgumentException>(() => service.EnqueueAsync(
            IntegrationMessageType.InventorySync,
            new ExternalIntegrationRequest("ERP", "v2", "sync-001", null, document.RootElement.Clone())));
    }
}
