using Warehouse.Wms.Application.Devices;
using Warehouse.Wms.Domain.Devices;

namespace Warehouse.Wms.DeviceGateway;

public sealed class SimulatedDeviceGateway : IWarehouseDeviceGateway
{
    private readonly SimulationScenario _scenario;
    private readonly SimulationStateStore _state;

    public SimulatedDeviceGateway(
        SimulationScenario? scenario = null,
        SimulationStateStore? state = null)
    {
        _scenario = scenario ?? new SimulationScenario();
        _state = state ?? new SimulationStateStore();
        if (_scenario.ResponseDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(scenario), "Response delay cannot be negative.");
        }
    }

    public Task<DeviceOperationResult> SubmitInboundAsync(
        DeviceTask task,
        CancellationToken cancellationToken = default) => SubmitAsync(task, cancellationToken);

    public Task<DeviceOperationResult> SubmitOutboundAsync(
        DeviceTask task,
        CancellationToken cancellationToken = default) => SubmitAsync(task, cancellationToken);

    public Task<DeviceOperationResult> SubmitTransferAsync(
        DeviceTask task,
        CancellationToken cancellationToken = default) => SubmitAsync(task, cancellationToken);

    public async Task<DeviceResultObservation?> GetStatusAsync(
        string deviceTaskNumber,
        CancellationToken cancellationToken = default)
    {
        await DelayAsync(cancellationToken);

        if (!_scenario.Capabilities.HasFlag(DeviceCapability.TaskQuery))
        {
            return null;
        }

        var command = _state.Commands.Values.FirstOrDefault(item =>
            string.Equals(item.DeviceTaskNumber, deviceTaskNumber, StringComparison.Ordinal));
        if (command is null)
        {
            return null;
        }

        return new DeviceResultObservation(
            command.DeviceTaskNumber,
            resultVersion: 1,
            command.CompletionStatus,
            DeviceObservationSource.Polling,
            DateTimeOffset.UtcNow);
    }

    public async Task<DeviceOperationResult> TestConnectionAsync(
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw new ArgumentException("A device identifier is required.", nameof(deviceId));
        }

        await DelayAsync(cancellationToken);
        return new DeviceOperationResult(
            $"connection:{deviceId}",
            _scenario.ConnectionStatus,
            errorCode: ErrorCodeFor(_scenario.ConnectionStatus));
    }

    public async Task<DeviceOperationResult> RequestStopAsync(
        DeviceTask task,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        await DelayAsync(cancellationToken);

        if (!_state.Commands.TryGetValue(task.IdempotencyKey, out var command))
        {
            return new DeviceOperationResult(task.IdempotencyKey, DeviceOperationStatus.Unknown);
        }

        if (!_scenario.Capabilities.HasFlag(DeviceCapability.StopControl))
        {
            return new DeviceOperationResult(
                task.IdempotencyKey,
                DeviceOperationStatus.Failed,
                command.DeviceTaskNumber,
                "SIM-STOP-UNSUPPORTED");
        }

        return new DeviceOperationResult(
            task.IdempotencyKey,
            command.StopStatus,
            command.DeviceTaskNumber,
            ErrorCodeFor(command.StopStatus));
    }

    public Task<DeviceCapability> GetCapabilitiesAsync() => Task.FromResult(_scenario.Capabilities);

    public int GetPhysicalActionCount(string idempotencyKey)
    {
        return _state.CountActions(idempotencyKey);
    }

    private async Task<DeviceOperationResult> SubmitAsync(
        DeviceTask task,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        await DelayAsync(cancellationToken);

        var supportsDeduplication = _scenario.Capabilities.HasFlag(DeviceCapability.TaskKeyDeduplication);
        if (supportsDeduplication && _state.Commands.TryGetValue(task.IdempotencyKey, out var existing))
        {
            return existing.SubmissionResult;
        }

        var commandKey = supportsDeduplication
            ? task.IdempotencyKey
            : $"{task.IdempotencyKey}#{Guid.NewGuid():N}";
        var deviceTaskNumber = supportsDeduplication
            ? $"sim-{task.WmsTaskId}"
            : $"sim-{task.WmsTaskId}-{Guid.NewGuid():N}";
        var status = _scenario.SubmissionStatus;
        var result = new DeviceOperationResult(
            task.IdempotencyKey,
            status,
            deviceTaskNumber,
            ErrorCodeFor(status));
        var command = new SimulatedCommand(
            task,
            deviceTaskNumber,
            result,
            _scenario.CompletionStatus,
            _scenario.StopStatus,
            _scenario.AlarmCode);

        if (_state.Commands.TryAdd(commandKey, command))
        {
            return result;
        }

        return _state.Commands[commandKey].SubmissionResult;
    }

    private async Task DelayAsync(CancellationToken cancellationToken)
    {
        if (_scenario.ResponseDelay > TimeSpan.Zero)
        {
            await Task.Delay(_scenario.ResponseDelay, cancellationToken);
        }
    }

    private string? ErrorCodeFor(DeviceOperationStatus status)
    {
        return status switch
        {
            DeviceOperationStatus.Failed => _scenario.AlarmCode ?? "SIM-FAILED",
            DeviceOperationStatus.Offline => "SIM-OFFLINE",
            DeviceOperationStatus.TimedOut => "SIM-TIMEOUT",
            DeviceOperationStatus.Unknown => "SIM-UNKNOWN",
            DeviceOperationStatus.StopFailed => "SIM-STOP-FAILED",
            DeviceOperationStatus.PhysicalStateUnknown => "SIM-PHYSICAL-UNKNOWN",
            _ => null
        };
    }
}
