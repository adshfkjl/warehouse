using System.Text.Json;
using Warehouse.Wms.Application.Integrations;
using Warehouse.Wms.Infrastructure.Persistence;

namespace Warehouse.Wms.Infrastructure.Integrations;

internal static class IntegrationOutboxPayload
{
    public static string Serialize(IntegrationMessage message)
        => JsonSerializer.Serialize(new
        {
            message.Type,
            message.Source,
            message.Version,
            message.CorrelationId,
            message.Payload,
            message.PayloadSha256
        });

    public static void EnsureReplayDigest(OutboxMessage persisted, IntegrationMessage incoming)
    {
        using var document = JsonDocument.Parse(persisted.Payload);
        if (!document.RootElement.TryGetProperty("PayloadSha256", out var digest)
            || !string.Equals(digest.GetString(), incoming.PayloadSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The integration idempotency key was already used for a different payload digest.");
        }
    }
}
