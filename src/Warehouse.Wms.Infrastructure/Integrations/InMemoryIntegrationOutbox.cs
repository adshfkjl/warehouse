using System.Text.Json;
using Warehouse.Wms.Application.Integrations;
using Warehouse.Wms.Infrastructure.Persistence;

namespace Warehouse.Wms.Infrastructure.Integrations;

public sealed class InMemoryIntegrationOutbox : IIntegrationOutbox
{
    private readonly object _sync = new();
    private readonly Dictionary<string, OutboxMessage> _messages = new(StringComparer.Ordinal);

    public Task<IntegrationEnqueueResult> EnqueueAsync(IntegrationMessage message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(message);
        lock (_sync)
        {
            if (_messages.ContainsKey(message.IdempotencyKey))
            {
                return Task.FromResult(new IntegrationEnqueueResult(true, true, "AlreadyQueued", message.IdempotencyKey));
            }

            var payload = JsonSerializer.Serialize(new
            {
                message.Type,
                message.Source,
                message.Version,
                message.CorrelationId,
                message.Payload,
                message.PayloadSha256
            });
            _messages.Add(message.IdempotencyKey, new OutboxMessage(
                $"integration.{message.Type}",
                "ExternalIntegration",
                message.IdempotencyKey,
                message.IdempotencyKey,
                payload));
            return Task.FromResult(new IntegrationEnqueueResult(true, false, "Queued", message.IdempotencyKey));
        }
    }

    public IReadOnlyCollection<OutboxMessage> Snapshot()
    {
        lock (_sync)
        {
            return _messages.Values.ToArray();
        }
    }
}
