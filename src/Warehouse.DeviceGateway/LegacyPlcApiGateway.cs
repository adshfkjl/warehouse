using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Warehouse.Wms.Application.Devices;
using Warehouse.Wms.DeviceGateway.Legacy;
using Warehouse.Wms.Domain.Devices;

namespace Warehouse.Wms.DeviceGateway;

public sealed class LegacyPlcApiGateway : IWarehouseDeviceGateway
{
    private readonly LegacyPlcClient _client;
    private readonly DeviceCapability _capabilities;
    private readonly string? _statusDeviceId;
    private long _statusResultVersion;

    public LegacyPlcApiGateway(
        LegacyPlcClient client,
        DeviceCapability capabilities = DeviceCapability.None,
        string? statusDeviceId = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _capabilities = capabilities;
        _statusDeviceId = string.IsNullOrWhiteSpace(statusDeviceId) ? null : statusDeviceId.Trim();
    }

    public Task<DeviceOperationResult> SubmitInboundAsync(
        DeviceTask task,
        CancellationToken cancellationToken = default) =>
        SubmitOperationAsync(
            task,
            task.DestinationLocation,
            nameof(DeviceTask.DestinationLocation),
            (coordinates, loadingPoint) => _client.SubmitInboundAsync(
                new LegacyInboundRequest(task.DeviceId, coordinates.Shelf, coordinates.Position, loadingPoint),
                cancellationToken),
            cancellationToken);

    public Task<DeviceOperationResult> SubmitOutboundAsync(
        DeviceTask task,
        CancellationToken cancellationToken = default) =>
        SubmitOperationAsync(
            task,
            task.SourceLocation,
            nameof(DeviceTask.SourceLocation),
            (coordinates, loadingPoint) => _client.SubmitOutboundAsync(
                new LegacyOutboundRequest(task.DeviceId, coordinates.Shelf, coordinates.Position, loadingPoint),
                cancellationToken),
            cancellationToken);

    public Task<DeviceOperationResult> SubmitTransferAsync(
        DeviceTask task,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        var source = ParseLocation(task.SourceLocation, nameof(task.SourceLocation));
        var destination = ParseLocation(task.DestinationLocation, nameof(task.DestinationLocation));
        var request = new LegacyTransferRequest(
            task.DeviceId,
            source.Shelf,
            source.Position,
            destination.Shelf,
            destination.Position);

        return SendAsync(task, () => _client.SubmitTransferAsync(request, cancellationToken), cancellationToken);
    }

    public async Task<DeviceResultObservation?> GetStatusAsync(
        string deviceTaskNumber,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceTaskNumber);
        if (!_capabilities.HasFlag(DeviceCapability.TaskQuery) || _statusDeviceId is null)
        {
            return null;
        }

        try
        {
            using var response = await _client.GetStatusAsync(_statusDeviceId, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var status = await response.Content.ReadFromJsonAsync<LegacyStatusResponse>(LegacyJson.Options, cancellationToken);
            return status is null
                ? null
                : new DeviceResultObservation(
                    deviceTaskNumber,
                    resultVersion: Interlocked.Increment(ref _statusResultVersion),
                    MapStatus(status),
                    DeviceObservationSource.Polling,
                    DateTimeOffset.UtcNow);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<DeviceOperationResult> TestConnectionAsync(
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        var task = new DeviceTask(
            $"connection:{deviceId}",
            $"connection:{deviceId}",
            deviceId,
            null,
            null,
            null,
            "legacy-v1");
        var result = await SendAsync(
            task,
            () => _client.TestConnectionAsync(deviceId, cancellationToken),
            cancellationToken,
            physicalAction: false);
        return result.Status == DeviceOperationStatus.Accepted
            ? new DeviceOperationResult(
                result.IdempotencyKey,
                DeviceOperationStatus.Succeeded,
                errorMessage: result.ErrorMessage)
            : result;
    }

    public Task<DeviceOperationResult> RequestStopAsync(
        DeviceTask task,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        return Task.FromResult(new DeviceOperationResult(
            task.IdempotencyKey,
            DeviceOperationStatus.Failed,
            errorCode: "LEGACY_STOP_UNSUPPORTED",
            errorMessage: "Legacy PLC API has no stop contract."));
    }

    private static async Task<DeviceOperationResult> SubmitOperationAsync(
        DeviceTask task,
        string? locationValue,
        string locationParameterName,
        Func<(int Shelf, int Position), int, Task<HttpResponseMessage>> send,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        var location = ParseLocation(locationValue, locationParameterName);
        var loadingPoint = ParseLoadingPoint(task.LoadingPoint);
        return await SendAsync(task, () => send(location, loadingPoint), cancellationToken);
    }

    private static async Task<DeviceOperationResult> SendAsync(
        DeviceTask task,
        Func<Task<HttpResponseMessage>> send,
        CancellationToken cancellationToken,
        bool physicalAction = true)
    {
        try
        {
            using var response = await send();
            LegacyOperationResponse? body = null;
            try
            {
                body = await ReadOperationResponseAsync(response);
            }
            catch (JsonException ex) when (response.IsSuccessStatusCode)
            {
                return Result(task, DeviceOperationStatus.Unknown, "LEGACY_INVALID_RESPONSE", ex.Message);
            }
            catch (JsonException)
            {
                // HTTP status remains authoritative for error responses whose body is not JSON.
                body = null;
            }

            if (response.StatusCode == HttpStatusCode.ServiceUnavailable ||
                response.StatusCode == HttpStatusCode.GatewayTimeout)
            {
                return Result(task, DeviceOperationStatus.Offline, $"HTTP_{(int)response.StatusCode}", body?.Message);
            }

            if (!response.IsSuccessStatusCode)
            {
                var busy = body?.Message?.Contains("busy", StringComparison.OrdinalIgnoreCase) == true ||
                           body?.Message?.Contains('忙') == true;
                return Result(task, DeviceOperationStatus.Failed, busy ? "PLC_BUSY" : $"HTTP_{(int)response.StatusCode}", body?.Message);
            }

            if (body is not null && body.IsSuccess is null)
            {
                return Result(task, DeviceOperationStatus.Unknown, "LEGACY_INVALID_RESPONSE", "Legacy PLC API response did not include isSuccess.");
            }

            if (body is not null && body.IsSuccess is false)
            {
                var busy = body.Message?.Contains("busy", StringComparison.OrdinalIgnoreCase) == true ||
                           body.Message?.Contains('忙') == true;
                return Result(task, DeviceOperationStatus.Failed, busy ? "PLC_BUSY" : "LEGACY_OPERATION_FAILED", body.Message);
            }

            if (body is null)
            {
                return Result(task, DeviceOperationStatus.Unknown, "LEGACY_INVALID_RESPONSE", "Legacy PLC API returned no operation result.");
            }

            return Result(task, DeviceOperationStatus.Accepted, null, body.Message);
        }
        catch (TaskCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            return Result(task, DeviceOperationStatus.Failed, "LEGACY_REQUEST_CANCELED", ex.Message);
        }
        catch (TaskCanceledException ex)
        {
            return Result(
                task,
                physicalAction ? DeviceOperationStatus.PhysicalStateUnknown : DeviceOperationStatus.TimedOut,
                physicalAction ? "LEGACY_TIMEOUT" : "LEGACY_CONNECTION_TIMEOUT",
                ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return Result(task, DeviceOperationStatus.Offline, "LEGACY_HTTP_ERROR", ex.Message);
        }
        catch (JsonException ex)
        {
            return Result(task, DeviceOperationStatus.Unknown, "LEGACY_INVALID_RESPONSE", ex.Message);
        }
    }

    private static async Task<LegacyOperationResponse?> ReadOperationResponseAsync(HttpResponseMessage response)
    {
        return await response.Content.ReadFromJsonAsync<LegacyOperationResponse>(LegacyJson.Options);
    }

    private static DeviceOperationResult Result(
        DeviceTask task,
        DeviceOperationStatus status,
        string? errorCode,
        string? message)
    {
        return new DeviceOperationResult(
            task.IdempotencyKey,
            status,
            deviceTaskNumber: status == DeviceOperationStatus.Accepted ? $"legacy-{task.WmsTaskId}" : null,
            errorCode,
            message);
    }

    private static DeviceOperationStatus MapStatus(LegacyStatusResponse status)
    {
        if (!status.Online)
        {
            return DeviceOperationStatus.Offline;
        }

        if (status.ErrorCode != 0)
        {
            return DeviceOperationStatus.Failed;
        }

        if (status.InboundCompleted || status.OutboundCompleted || status.TransferCompleted)
        {
            return DeviceOperationStatus.Succeeded;
        }

        return status.Working != 0 || status.TaskStatus != 0
            ? DeviceOperationStatus.Executing
            : DeviceOperationStatus.Accepted;
    }

    private static (int Shelf, int Position) ParseLocation(string? value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var parts = value.Split('-', StringSplitOptions.TrimEntries);
        if (parts.Length < 2 ||
            parts.Any(string.IsNullOrWhiteSpace) ||
            !int.TryParse(parts[^2], out var shelf) ||
            !int.TryParse(parts[^1], out var position) ||
            shelf < 0 ||
            position < 0)
        {
            throw new ArgumentException("Location must end with numeric shelf and position segments.", parameterName);
        }

        return (shelf, position);
    }

    private static int ParseLoadingPoint(string? value)
    {
        if (!int.TryParse(value, out var loadingPoint) || loadingPoint is < 0 or > 1)
        {
            throw new ArgumentException("Loading point must be numeric.", nameof(value));
        }

        return loadingPoint;
    }
}
