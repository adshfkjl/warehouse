using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Warehouse.Wms.Application.Integrations;

public enum IntegrationMessageType
{
    InboundNotice,
    OutboundRequest,
    CancelRequest,
    StatusQuery,
    ResultCallback,
    InventorySync
}

public sealed record ExternalIntegrationRequest(
    string Source,
    string Version,
    string IdempotencyKey,
    string? CorrelationId,
    JsonElement Payload);

public sealed record IntegrationMessage(
    IntegrationMessageType Type,
    string Source,
    string Version,
    string IdempotencyKey,
    string? CorrelationId,
    string Payload,
    string PayloadSha256);

public sealed record IntegrationEnqueueResult(
    bool Accepted,
    bool Duplicate,
    string Status,
    string IdempotencyKey);

public interface IIntegrationOutbox
{
    Task<IntegrationEnqueueResult> EnqueueAsync(IntegrationMessage message, CancellationToken cancellationToken = default);
}

public interface IIntegrationCommandService
{
    Task<IntegrationEnqueueResult> EnqueueAsync(
        IntegrationMessageType type,
        ExternalIntegrationRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class IntegrationCommandService : IIntegrationCommandService
{
    private readonly IIntegrationOutbox _outbox;
    private readonly bool _enabled;

    public IntegrationCommandService(IIntegrationOutbox outbox, bool enabled = false)
    {
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _enabled = enabled;
    }

    public Task<IntegrationEnqueueResult> EnqueueAsync(
        IntegrationMessageType type,
        ExternalIntegrationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_enabled)
        {
            return Task.FromResult(new IntegrationEnqueueResult(false, false, "Disabled", request.IdempotencyKey));
        }

        var source = Require(request.Source, nameof(request.Source));
        var version = Require(request.Version, nameof(request.Version));
        var key = Require(request.IdempotencyKey, nameof(request.IdempotencyKey));
        if (!string.Equals(version, "v1", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Only integration contract version v1 is supported.", nameof(request));
        }

        var payload = request.Payload.ValueKind == JsonValueKind.Undefined
            ? "{}"
            : request.Payload.GetRawText();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        return _outbox.EnqueueAsync(new IntegrationMessage(type, source, version, key, request.CorrelationId, payload, hash), cancellationToken);
    }

    private static string Require(string value, string name)
        => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A non-empty value is required.", name) : value.Trim();
}
