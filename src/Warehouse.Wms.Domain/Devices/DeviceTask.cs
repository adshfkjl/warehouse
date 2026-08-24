namespace Warehouse.Wms.Domain.Devices;

public sealed class DeviceTask
{
    public DeviceTask(
        string idempotencyKey,
        string wmsTaskId,
        string deviceId,
        string? sourceLocation,
        string? destinationLocation,
        string? loadingPoint,
        string protocolVersion)
    {
        IdempotencyKey = Require(idempotencyKey, nameof(idempotencyKey));
        WmsTaskId = Require(wmsTaskId, nameof(wmsTaskId));
        DeviceId = Require(deviceId, nameof(deviceId));
        ProtocolVersion = Require(protocolVersion, nameof(protocolVersion));
        SourceLocation = sourceLocation;
        DestinationLocation = destinationLocation;
        LoadingPoint = loadingPoint;
    }

    public string IdempotencyKey { get; }

    public string WmsTaskId { get; }

    public string DeviceId { get; }

    public string? SourceLocation { get; }

    public string? DestinationLocation { get; }

    public string? LoadingPoint { get; }

    public string ProtocolVersion { get; }

    private static string Require(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty value is required.", parameterName);
        }

        return value.Trim();
    }
}
