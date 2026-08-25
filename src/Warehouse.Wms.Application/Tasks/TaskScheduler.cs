using Warehouse.Wms.Application.Devices;
using Warehouse.Wms.Domain.Devices;
using Warehouse.Wms.Domain.Tasks;

namespace Warehouse.Wms.Application.Tasks;

public enum DeviceOperationKind
{
    Inbound,
    Outbound,
    Transfer
}

public sealed class TaskDispatchRequest
{
    public TaskDispatchRequest(
        WarehouseTask task,
        DeviceTask deviceTask,
        DeviceOperationKind operationKind,
        int priority = 0,
        int maxAttempts = 2)
    {
        Task = task ?? throw new ArgumentNullException(nameof(task));
        DeviceTask = deviceTask ?? throw new ArgumentNullException(nameof(deviceTask));
        if (maxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), maxAttempts, "At least one dispatch attempt is required.");
        }

        OperationKind = operationKind;
        Priority = priority;
        MaxAttempts = maxAttempts;
    }

    public WarehouseTask Task { get; }

    public DeviceTask DeviceTask { get; }

    public DeviceOperationKind OperationKind { get; }

    public int Priority { get; }

    public int MaxAttempts { get; }

    public int AttemptCount { get; internal set; }

    public string? DeviceTaskNumber { get; internal set; }
}

public sealed record TaskDispatchResult(
    TaskDispatchRequest Request,
    DeviceOperationStatus Status,
    string? DeviceTaskNumber,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public sealed class TaskSchedulerState
{
    internal Dictionary<string, TaskDispatchRequest> Requests { get; } = new(StringComparer.Ordinal);

    internal Dictionary<string, long> LatestObservationVersions { get; } = new(StringComparer.Ordinal);

    internal HashSet<string> ActiveDevices { get; } = new(StringComparer.Ordinal);
}

public sealed class TaskScheduler
{
    private static readonly TimeSpan DefaultOutboxLease = TimeSpan.FromMinutes(2);
    private readonly object _gate = new();
    private readonly IWarehouseDeviceGateway _gateway;
    private readonly DeviceCapability _capabilities;
    private readonly TaskSchedulerState _state;
    private readonly ITaskCommandOutbox? _commandOutbox;
    private readonly string _workerId;
    private readonly TimeSpan _outboxLease;

    public TaskScheduler(
        IWarehouseDeviceGateway gateway,
        DeviceCapability capabilities = DeviceCapability.TaskKeyDeduplication | DeviceCapability.TaskQuery,
        TaskSchedulerState? state = null,
        ITaskCommandOutbox? commandOutbox = null,
        string? workerId = null,
        TimeSpan? outboxLease = null)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _capabilities = capabilities;
        _state = state ?? new TaskSchedulerState();
        _commandOutbox = commandOutbox;
        _workerId = string.IsNullOrWhiteSpace(workerId) ? Environment.MachineName + ":scheduler" : workerId.Trim();
        _outboxLease = outboxLease ?? DefaultOutboxLease;
        if (_outboxLease <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(outboxLease), _outboxLease, "Outbox lease must be positive.");
        }
    }

    public Task EnqueueAsync(TaskDispatchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_state.Requests.TryGetValue(request.Task.TaskNumber, out var existing))
            {
                if (!string.Equals(existing.DeviceTask.IdempotencyKey, request.DeviceTask.IdempotencyKey, StringComparison.Ordinal)
                    || !string.Equals(existing.DeviceTask.DeviceId, request.DeviceTask.DeviceId, StringComparison.Ordinal)
                    || existing.OperationKind != request.OperationKind)
                {
                    throw new InvalidOperationException(
                        $"Task '{request.Task.TaskNumber}' is already queued with a different device command.");
                }

                return Task.CompletedTask;
            }

            if (request.Task.State == TaskState.Created)
            {
                request.Task.TransitionTo(TaskState.Allocated, "scheduler", "resources allocated");
            }

            if (request.Task.State == TaskState.Allocated)
            {
                request.Task.TransitionTo(TaskState.Queued, "scheduler", "queued for dispatch");
            }

            if (request.Task.State != TaskState.Queued)
            {
                throw new InvalidOperationException($"Task '{request.Task.TaskNumber}' is not queueable from '{request.Task.State}'.");
            }

            _state.Requests.Add(request.Task.TaskNumber, request);
            return Task.CompletedTask;
        }
    }

    public async Task<TaskDispatchResult?> DispatchNextAsync(CancellationToken cancellationToken = default)
    {
        TaskDispatchRequest? request;
        lock (_gate)
        {
            request = _state.Requests.Values
                .Where(candidate => candidate.Task.State == TaskState.Queued && !_state.ActiveDevices.Contains(candidate.DeviceTask.DeviceId))
                .OrderByDescending(candidate => candidate.Priority)
                .ThenBy(candidate => candidate.Task.TaskNumber, StringComparer.Ordinal)
                .FirstOrDefault();

            if (request is null)
            {
                return null;
            }

            request.Task.TransitionTo(TaskState.Dispatching, "scheduler", "dispatch attempt started");
            request.AttemptCount++;
            _state.ActiveDevices.Add(request.DeviceTask.DeviceId);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                request.Task.TransitionTo(TaskState.Queued, "scheduler", "dispatch canceled before device call", "DISPATCH_CANCELED");
                _state.ActiveDevices.Remove(request.DeviceTask.DeviceId);
            }

            throw;
        }

        TaskCommandOutboxClaim? commandClaim = null;
        if (_commandOutbox is not null)
        {
            commandClaim = await _commandOutbox.EnqueueAndClaimAsync(
                request.DeviceTask,
                request.OperationKind,
                _workerId,
                DateTimeOffset.UtcNow,
                _outboxLease,
                cancellationToken);
            if (commandClaim is null)
            {
                lock (_gate)
                {
                    request.Task.TransitionTo(TaskState.Queued, "scheduler", "device command is already claimed or published", "COMMAND_NOT_DISPATCHABLE");
                    _state.ActiveDevices.Remove(request.DeviceTask.DeviceId);
                }

                return null;
            }
        }

        DeviceOperationResult result;
        try
        {
            result = request.OperationKind switch
            {
                DeviceOperationKind.Inbound => await _gateway.SubmitInboundAsync(request.DeviceTask, cancellationToken),
                DeviceOperationKind.Outbound => await _gateway.SubmitOutboundAsync(request.DeviceTask, cancellationToken),
                DeviceOperationKind.Transfer => await _gateway.SubmitTransferAsync(request.DeviceTask, cancellationToken),
                _ => throw new InvalidOperationException($"Unsupported device operation kind '{request.OperationKind}'.")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (commandClaim is not null)
            {
                await _commandOutbox!.MarkFailedAsync(commandClaim, "DISPATCH_CANCELED", DateTimeOffset.UtcNow, DateTimeOffset.MaxValue, CancellationToken.None);
            }
            lock (_gate)
            {
                request.Task.TransitionTo(TaskState.TimedOut, "scheduler", "device call canceled before result", "DISPATCH_CANCELED");
                request.Task.TransitionTo(TaskState.PhysicalStateUnknown, "scheduler", "physical result requires reconciliation", "PHYSICAL_UNKNOWN");
            }

            throw;
        }
        catch (Exception exception)
        {
            if (commandClaim is not null)
            {
                await _commandOutbox!.MarkFailedAsync(commandClaim, exception.Message, DateTimeOffset.UtcNow, DateTimeOffset.MaxValue, CancellationToken.None);
            }
            lock (_gate)
            {
                request.Task.TransitionTo(TaskState.TimedOut, "scheduler", "device call failed without a definitive result", "DEVICE_CALL_UNKNOWN");
                request.Task.TransitionTo(TaskState.PhysicalStateUnknown, "scheduler", "physical result requires reconciliation", "PHYSICAL_UNKNOWN");
            }

            return new TaskDispatchResult(
                request,
                DeviceOperationStatus.PhysicalStateUnknown,
                request.DeviceTaskNumber,
                "DEVICE_CALL_UNKNOWN",
                exception.Message);
        }

        TaskDispatchResult dispatchResult;
        var markPublished = false;
        string? outboxFailure = null;
        var nextAttemptAt = DateTimeOffset.MaxValue;
        lock (_gate)
        {
            if (!string.Equals(result.IdempotencyKey, request.DeviceTask.IdempotencyKey, StringComparison.Ordinal))
            {
                request.Task.TransitionTo(TaskState.TimedOut, "scheduler", "device response idempotency key mismatch", "DEVICE_IDEMPOTENCY_MISMATCH");
                request.Task.TransitionTo(TaskState.PhysicalStateUnknown, "scheduler", "physical result requires reconciliation", "PHYSICAL_UNKNOWN");
                outboxFailure = "DEVICE_IDEMPOTENCY_MISMATCH";
                dispatchResult = new TaskDispatchResult(
                    request,
                    DeviceOperationStatus.PhysicalStateUnknown,
                    request.DeviceTaskNumber,
                    "DEVICE_IDEMPOTENCY_MISMATCH",
                    "The device response did not match the submitted command.");
            }
            else
            {
                if (result.Status == DeviceOperationStatus.Accepted)
                {
                    request.DeviceTaskNumber = result.DeviceTaskNumber;
                    request.Task.TransitionTo(TaskState.SentToPlc, "gateway", "device accepted command");
                    markPublished = true;
                }
                else if (result.Status == DeviceOperationStatus.Executing)
                {
                    request.DeviceTaskNumber = result.DeviceTaskNumber;
                    request.Task.TransitionTo(TaskState.SentToPlc, "gateway", "device accepted command");
                    request.Task.TransitionTo(TaskState.Executing, "gateway", "device execution reported");
                    markPublished = true;
                }
                else if (result.Status == DeviceOperationStatus.Succeeded)
                {
                    request.DeviceTaskNumber = result.DeviceTaskNumber;
                    request.Task.TransitionTo(TaskState.SentToPlc, "gateway", "device accepted and completed command");
                    request.Task.TransitionTo(TaskState.Executing, "gateway", "device completion reported");
                    request.Task.TransitionTo(TaskState.Succeeded, "gateway", "device completed");
                    _state.ActiveDevices.Remove(request.DeviceTask.DeviceId);
                    markPublished = true;
                }
                else if (result.Status is DeviceOperationStatus.PhysicalStateUnknown or DeviceOperationStatus.TimedOut or DeviceOperationStatus.Unknown)
                {
                    request.Task.TransitionTo(TaskState.TimedOut, "scheduler", "dispatch result cannot be confirmed", result.ErrorCode);
                    request.Task.TransitionTo(TaskState.PhysicalStateUnknown, "scheduler", "physical result requires reconciliation", result.ErrorCode);
                    outboxFailure = result.ErrorCode ?? result.Status.ToString();
                }
                else if (result.Status is DeviceOperationStatus.Failed or DeviceOperationStatus.Offline)
                {
                    var retryable = request.AttemptCount < request.MaxAttempts
                        && _capabilities.HasFlag(DeviceCapability.TaskKeyDeduplication)
                        && _capabilities.HasFlag(DeviceCapability.TaskQuery);
                    if (retryable)
                    {
                        request.Task.TransitionTo(TaskState.Queued, "scheduler", "dispatch will be retried", result.ErrorCode);
                        _state.ActiveDevices.Remove(request.DeviceTask.DeviceId);
                        nextAttemptAt = DateTimeOffset.UtcNow;
                    }
                    else
                    {
                        request.Task.TransitionTo(TaskState.Failed, "scheduler", "dispatch failed", result.ErrorCode);
                    }

                    _state.ActiveDevices.Remove(request.DeviceTask.DeviceId);
                    outboxFailure = result.ErrorCode ?? result.Status.ToString();
                }

                dispatchResult = new TaskDispatchResult(request, result.Status, request.DeviceTaskNumber ?? result.DeviceTaskNumber, result.ErrorCode, result.ErrorMessage);
            }
        }

        if (commandClaim is not null)
        {
            if (markPublished)
            {
                await _commandOutbox!.MarkPublishedAsync(commandClaim, DateTimeOffset.UtcNow, CancellationToken.None);
            }
            else if (outboxFailure is not null)
            {
                await _commandOutbox!.MarkFailedAsync(commandClaim, outboxFailure, DateTimeOffset.UtcNow, nextAttemptAt, CancellationToken.None);
            }
        }

        return dispatchResult;
    }

    internal Task<bool> ApplyObservationAsync(TaskDispatchRequest request, DeviceResultObservation observation)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(observation);
        lock (_gate)
        {
            if (!string.Equals(request.DeviceTaskNumber, observation.DeviceTaskNumber, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Device observation '{observation.DeviceTaskNumber}' does not belong to task '{request.Task.TaskNumber}'.");
            }

            if (_state.LatestObservationVersions.TryGetValue(observation.DeviceTaskNumber, out var latestVersion)
                && observation.ResultVersion <= latestVersion)
            {
                return Task.FromResult(false);
            }

            _state.LatestObservationVersions[observation.DeviceTaskNumber] = observation.ResultVersion;
            switch (observation.Status)
            {
                case DeviceOperationStatus.Executing:
                    if (request.Task.State == TaskState.SentToPlc)
                    {
                        request.Task.TransitionTo(TaskState.Executing, "device-observer", "device execution observed");
                    }

                    break;
                case DeviceOperationStatus.Succeeded:
                    if (request.Task.State == TaskState.SentToPlc)
                    {
                        request.Task.TransitionTo(TaskState.Executing, "device-observer", "completion observed");
                    }

                    if (request.Task.State == TaskState.Executing)
                    {
                        request.Task.TransitionTo(TaskState.Succeeded, "device-observer", "device completed");
                    }

                    _state.ActiveDevices.Remove(request.DeviceTask.DeviceId);
                    break;
                case DeviceOperationStatus.Failed:
                    if (request.Task.State is TaskState.SentToPlc or TaskState.Executing)
                    {
                        request.Task.TransitionTo(TaskState.Failed, "device-observer", "device failed");
                    }

                    _state.ActiveDevices.Remove(request.DeviceTask.DeviceId);
                    break;
                case DeviceOperationStatus.TimedOut:
                case DeviceOperationStatus.Offline:
                case DeviceOperationStatus.Unknown:
                    if (request.Task.State is TaskState.SentToPlc or TaskState.Executing)
                    {
                        request.Task.TransitionTo(TaskState.TimedOut, "device-observer", "device result cannot be confirmed");
                    }

                    if (request.Task.State == TaskState.TimedOut)
                    {
                        request.Task.TransitionTo(TaskState.PhysicalStateUnknown, "device-observer", "physical state unknown");
                    }

                    break;
                case DeviceOperationStatus.PhysicalStateUnknown:
                    if (request.Task.State is TaskState.SentToPlc or TaskState.Executing)
                    {
                        request.Task.TransitionTo(TaskState.TimedOut, "device-observer", "device result cannot be reconciled");
                    }

                    if (request.Task.State == TaskState.TimedOut)
                    {
                        request.Task.TransitionTo(TaskState.PhysicalStateUnknown, "device-observer", "physical state unknown");
                    }

                    break;
            }

            return Task.FromResult(true);
        }
    }

    public async Task RecoverInFlightAsync(CancellationToken cancellationToken = default)
    {
        TaskDispatchRequest[] inFlight;
        lock (_gate)
        {
            inFlight = _state.Requests.Values
                .Where(request => request.Task.State is TaskState.Dispatching or TaskState.SentToPlc or TaskState.Executing)
                .ToArray();
        }

        foreach (var request in inFlight)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_capabilities.HasFlag(DeviceCapability.TaskQuery) || string.IsNullOrWhiteSpace(request.DeviceTaskNumber))
            {
                lock (_gate)
                {
                    if (request.Task.State is TaskState.Dispatching or TaskState.SentToPlc or TaskState.Executing)
                    {
                        request.Task.TransitionTo(TaskState.TimedOut, "worker", "restart cannot query device task", "DEVICE_QUERY_UNSUPPORTED");
                        request.Task.TransitionTo(TaskState.PhysicalStateUnknown, "worker", "physical result requires reconciliation", "PHYSICAL_UNKNOWN");
                    }
                }

                continue;
            }

            DeviceResultObservation? observation;
            try
            {
                observation = await _gateway.GetStatusAsync(request.DeviceTaskNumber, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                observation = null;
            }

            if (observation is not null)
            {
                await ApplyObservationAsync(request, observation);
            }
            else
            {
                lock (_gate)
                {
                    if (request.Task.State is TaskState.Dispatching or TaskState.SentToPlc or TaskState.Executing)
                    {
                        request.Task.TransitionTo(TaskState.TimedOut, "worker", "restart device query returned no definitive result", "DEVICE_QUERY_UNKNOWN");
                        request.Task.TransitionTo(TaskState.PhysicalStateUnknown, "worker", "physical result requires reconciliation", "PHYSICAL_UNKNOWN");
                    }
                }
            }
        }
    }
}
