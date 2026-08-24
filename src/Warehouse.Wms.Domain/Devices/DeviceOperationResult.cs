namespace Warehouse.Wms.Domain.Devices;

public enum DeviceOperationStatus
{
    Accepted,
    Executing,
    Succeeded,
    Failed,
    TimedOut,
    Offline,
    Unknown,
    StopConfirmed,
    StopFailed,
    PhysicalStateUnknown
}

public enum DeviceObservationSource
{
    Polling,
    Callback
}

public sealed class DeviceResultObservation
{
    public DeviceResultObservation(
        string deviceTaskNumber,
        long resultVersion,
        DeviceOperationStatus status,
        DeviceObservationSource source,
        DateTimeOffset observedAt)
    {
        if (string.IsNullOrWhiteSpace(deviceTaskNumber))
        {
            throw new ArgumentException("A device task number is required.", nameof(deviceTaskNumber));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(resultVersion);

        DeviceTaskNumber = deviceTaskNumber.Trim();
        ResultVersion = resultVersion;
        Status = status;
        Source = source;
        ObservedAt = observedAt;
    }

    public string DeviceTaskNumber { get; }

    public long ResultVersion { get; }

    public DeviceOperationStatus Status { get; }

    public DeviceObservationSource Source { get; }

    public DateTimeOffset ObservedAt { get; }
}

public sealed class DeviceOperationResult
{
    public DeviceOperationResult(
        string idempotencyKey,
        DeviceOperationStatus status,
        string? deviceTaskNumber = null,
        string? errorCode = null,
        string? errorMessage = null,
        DeviceResultObservation? observation = null)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new ArgumentException("An idempotency key is required.", nameof(idempotencyKey));
        }

        IdempotencyKey = idempotencyKey.Trim();
        Status = status;
        DeviceTaskNumber = deviceTaskNumber;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
        Observation = observation;
    }

    public string IdempotencyKey { get; }

    public DeviceOperationStatus Status { get; }

    public string? DeviceTaskNumber { get; }

    public string? ErrorCode { get; }

    public string? ErrorMessage { get; }

    public DeviceResultObservation? Observation { get; }
}
