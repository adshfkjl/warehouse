using Warehouse.Wms.Application.Devices;
using Warehouse.Wms.Domain.Devices;
using Warehouse.Wms.Domain.Tasks;

namespace Warehouse.Wms.Application.Tasks;

/// <summary>
/// Releases scheduler-owned reservations after a cancellation has been proven safe.
/// The implementation that owns resource locks is supplied by the host; this service
/// never releases physical resources for an unknown device result.
/// </summary>
public interface ITaskResourceReleaseCoordinator
{
    void ReleaseSchedulingResources(TaskDispatchRequest request);
}

public sealed record TaskCancellationResult(
    TaskDispatchRequest Request,
    TaskState State,
    DeviceOperationStatus? DeviceStatus,
    bool StopRequested,
    bool ResourcesReleased,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public sealed class TaskCancellationService : IDisposable
{
    public const string CancellationOperation = "Task.Cancel";

    private readonly IWarehouseDeviceGateway _gateway;
    private readonly ITaskResourceReleaseCoordinator? _resourceRelease;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TaskCancellationService(
        IWarehouseDeviceGateway gateway,
        ITaskResourceReleaseCoordinator? resourceRelease = null)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _resourceRelease = resourceRelease;
    }

    public void Dispose() => _gate.Dispose();

    /// <summary>
    /// Cancels a task only when its physical action is known not to have started.
    /// For an in-flight device task this method requests a physical stop and only
    /// releases resources after an explicit stop confirmation.
    /// </summary>
    public async Task<TaskCancellationResult> CancelAsync(
        TaskDispatchRequest request,
        string @operator,
        string reason,
        bool deviceCallStarted = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Require(@operator, nameof(@operator));
        Require(reason, nameof(reason));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await CancelCoreAsync(request, @operator.Trim(), reason.Trim(), deviceCallStarted, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<TaskCancellationResult> CancelCoreAsync(
        TaskDispatchRequest request,
        string @operator,
        string reason,
        bool deviceCallStarted,
        CancellationToken cancellationToken)
    {
        var task = request.Task;
        switch (task.State)
        {
            case TaskState.Created:
            case TaskState.Allocated:
            case TaskState.Queued:
                task.TransitionTo(TaskState.Canceled, @operator, reason, "TASK_CANCELED");
                return Release(request, TaskState.Canceled, null, false);

            case TaskState.Dispatching when !deviceCallStarted:
                // The scheduler has reserved a slot but has not entered the gateway call.
                task.TransitionTo(TaskState.Queued, @operator, reason, "DISPATCH_CANCELED");
                return Release(request, TaskState.Queued, null, false);

            case TaskState.Dispatching:
                // A started call has no definitive device task number yet. Treat it as
                // potentially delivered; never turn that uncertainty into cancellation.
                task.TransitionTo(TaskState.TimedOut, @operator, reason, "DISPATCH_CALL_UNKNOWN");
                task.TransitionTo(TaskState.PhysicalStateUnknown, @operator, "physical result requires reconciliation", "PHYSICAL_UNKNOWN");
                return Unknown(request, "DISPATCH_CALL_UNKNOWN", "The started device call has no definitive result.");

            case TaskState.SentToPlc:
            case TaskState.Executing:
                return await RequestStopAsync(request, @operator, reason, cancellationToken);

            case TaskState.PhysicalStateUnknown:
                throw new InvalidOperationException(
                    $"Task '{task.TaskNumber}' has an unknown physical state and cannot be canceled or released.");

            default:
                throw new InvalidOperationException(
                    $"Task '{task.TaskNumber}' cannot be canceled from state '{task.State}'.");
        }
    }

    private async Task<TaskCancellationResult> RequestStopAsync(
        TaskDispatchRequest request,
        string @operator,
        string reason,
        CancellationToken cancellationToken)
    {
        request.Task.TransitionTo(TaskState.CancelRequested, @operator, reason, "CANCEL_REQUESTED");
        request.Task.TransitionTo(TaskState.StopRequested, @operator, "stop requested from WMS", "STOP_REQUESTED");

        DeviceOperationResult stopResult;
        try
        {
            stopResult = await _gateway.RequestStopAsync(request.DeviceTask, cancellationToken);
        }
        catch (Exception exception)
        {
            request.Task.TransitionTo(TaskState.PhysicalStateUnknown, "device-gateway", "stop result cannot be reconciled", "STOP_RESULT_UNKNOWN");
            return Unknown(request, "STOP_RESULT_UNKNOWN", exception.Message, stopRequested: true);
        }

        if (!string.Equals(stopResult.IdempotencyKey, request.DeviceTask.IdempotencyKey, StringComparison.Ordinal))
        {
            request.Task.TransitionTo(TaskState.PhysicalStateUnknown, "device-gateway", "stop response idempotency key mismatch", "STOP_IDEMPOTENCY_MISMATCH");
            return Unknown(request, "STOP_IDEMPOTENCY_MISMATCH", "The stop response did not match the submitted command.", stopRequested: true);
        }

        switch (stopResult.Status)
        {
            case DeviceOperationStatus.StopConfirmed:
                request.Task.TransitionTo(TaskState.StopConfirmed, "device-gateway", "device confirmed stop", "STOP_CONFIRMED");
                request.Task.TransitionTo(TaskState.Canceled, @operator, "canceled after device stop confirmation", "TASK_CANCELED");
                return Release(request, TaskState.Canceled, stopResult.Status, true);

            case DeviceOperationStatus.StopFailed:
            case DeviceOperationStatus.Failed:
            case DeviceOperationStatus.Offline:
                request.Task.TransitionTo(TaskState.StopFailed, "device-gateway", "device did not confirm stop", stopResult.ErrorCode ?? "STOP_FAILED");
                return new TaskCancellationResult(
                    request, TaskState.StopFailed, stopResult.Status, true, false,
                    stopResult.ErrorCode, stopResult.ErrorMessage);

            case DeviceOperationStatus.PhysicalStateUnknown:
            case DeviceOperationStatus.TimedOut:
            case DeviceOperationStatus.Unknown:
            default:
                request.Task.TransitionTo(TaskState.PhysicalStateUnknown, "device-gateway", "stop result requires physical reconciliation", stopResult.ErrorCode ?? "STOP_RESULT_UNKNOWN");
                return Unknown(request, stopResult.ErrorCode ?? "STOP_RESULT_UNKNOWN", stopResult.ErrorMessage, true);
        }
    }

    private TaskCancellationResult Release(
        TaskDispatchRequest request,
        TaskState state,
        DeviceOperationStatus? deviceStatus,
        bool stopRequested)
    {
        var released = _resourceRelease is not null;
        _resourceRelease?.ReleaseSchedulingResources(request);
        return new TaskCancellationResult(request, state, deviceStatus, stopRequested, released);
    }

    private static TaskCancellationResult Unknown(
        TaskDispatchRequest request,
        string errorCode,
        string? errorMessage,
        bool stopRequested = false)
        => new(request, TaskState.PhysicalStateUnknown, DeviceOperationStatus.PhysicalStateUnknown, stopRequested, false, errorCode, errorMessage);

    private static string Require(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty value is required.", parameterName);
        }

        return value;
    }
}
