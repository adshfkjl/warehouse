using Warehouse.Wms.Application.Integrations;
using Warehouse.Wms.Infrastructure.Persistence;

namespace Warehouse.Wms.Infrastructure.Integrations;

/// <summary>
/// Durable external-integration outbox adapter. The message store owns each
/// SQL transaction; this adapter never publishes or waits on an external
/// system while a database transaction is open.
/// </summary>
public sealed class SqlServerIntegrationOutbox(IOutboxMessageStore messageStore) : IIntegrationOutbox
{
    public async Task<IntegrationEnqueueResult> EnqueueAsync(
        IntegrationMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var payload = IntegrationOutboxPayload.Serialize(message);
        var persisted = await messageStore.EnqueueAsync(
            new OutboxMessage(
                $"integration.{message.Type}",
                "ExternalIntegration",
                message.IdempotencyKey,
                message.IdempotencyKey,
                payload),
            cancellationToken);

        if (persisted.Replayed)
        {
            IntegrationOutboxPayload.EnsureReplayDigest(persisted.Message, message);
            return new IntegrationEnqueueResult(true, true, "AlreadyQueued", message.IdempotencyKey);
        }

        return new IntegrationEnqueueResult(true, false, "Queued", message.IdempotencyKey);
    }
}
