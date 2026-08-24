using Warehouse.Wms.Domain.Devices;

namespace Warehouse.Wms.DeviceGateway.Legacy;

public sealed class LegacyCapabilityProbe
{
    private static readonly string[] AllCapabilities =
    [
        nameof(DeviceCapability.TaskKeyDeduplication),
        nameof(DeviceCapability.TaskQuery),
        nameof(DeviceCapability.StopControl),
        nameof(DeviceCapability.CompletionCallback)
    ];

    private readonly LegacyPlcClient _client;

    public LegacyCapabilityProbe(LegacyPlcClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<LegacyCapabilityProbeResult> ProbeAsync(
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        string evidence;
        try
        {
            using var response = await _client.ProbeCapabilitiesAsync(cancellationToken);
            evidence = $"OPTIONS api/plc-operations returned HTTP {(int)response.StatusCode}; no explicit capability metadata was provided.";
        }
        catch (HttpRequestException ex)
        {
            evidence = $"Capability probe failed at transport boundary: {ex.Message}";
        }
        catch (TaskCanceledException ex)
        {
            evidence = $"Capability probe timed out: {ex.Message}";
        }

        // The legacy contract has no task-key or capability protocol.
        // Keep every capability blocked until a signed interface/field confirmation exists.
        return new LegacyCapabilityProbeResult(
            DeviceCapability.None,
            AllCapabilities,
            evidence);
    }
}

public sealed record LegacyCapabilityProbeResult(
    DeviceCapability Capabilities,
    IReadOnlyList<string> BlockedCapabilities,
    string Evidence);
