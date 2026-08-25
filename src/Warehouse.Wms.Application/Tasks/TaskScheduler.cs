using Warehouse.Wms.Application.Devices;
using Warehouse.Wms.Domain.Devices;
using Warehouse.Wms.Domain.Tasks;
using System.Text.Json;

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

internal sealed record PersistedDispatchContext(
    string IdempotencyKey, string WmsTaskId, string DeviceId, string? SourceLocation,
    string? DestinationLocation, string? LoadingPoint, string ProtocolVersion,
    DeviceOperationKind OperationKind, int Priority, int MaxAttempts, int AttemptCount,
    string? DeviceTaskNumber)
{
    public static string Serialize(TaskDispatchRequest request) => JsonSerializer.Serialize(new PersistedDispatchContext(
        request.DeviceTask.IdempotencyKey, request.DeviceTask.WmsTaskId, request.DeviceTask.DeviceId,
        request.DeviceTask.SourceLocation, request.DeviceTask.DestinationLocation, request.DeviceTask.LoadingPoint,
        request.DeviceTask.ProtocolVersion, request.OperationKind, request.Priority, request.MaxAttempts,
        request.AttemptCount, request.DeviceTaskNumber));

    public TaskDispatchRequest ToRequest(WarehouseTask task) => new(
        task,
        new DeviceTask(IdempotencyKey, WmsTaskId, DeviceId, SourceLocation, DestinationLocation, LoadingPoint, ProtocolVersion),
        OperationKind, Priority, MaxAttempts) { AttemptCount = AttemptCount, DeviceTaskNumber = DeviceTaskNumber };
}

public sealed class TaskSchedulerState
{
    internal Dictionary<string, TaskDispatchRequest> Requests { get; } = new(StringComparer.Ordinal);

    internal Dictionary<string, long> LatestObservationVersions { get; } = new(StringComparer.Ordinal);

    internal HashSet<string> ActiveDevices { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, ResourceLock> DeviceLeases { get; } = new(StringComparer.Ordinal);
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
    private readonly ITaskPersistenceStore? _persistenceStore;
    private readonly IResourceLockStore? _resourceLockStore;

    public TaskScheduler(
        IWarehouseDeviceGateway gateway,
        DeviceCapability capabilities = DeviceCapability.TaskKeyDeduplication | DeviceCapability.TaskQuery,
        TaskSchedulerState? state = null,
        ITaskCommandOutbox? commandOutbox = null,
        string? workerId = null,
        TimeSpan? outboxLease = null,
        ITaskPersistenceStore? persistenceStore = null,
        IResourceLockStore? resourceLockStore = null)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _capabilities = capabilities;
        _state = state ?? new TaskSchedulerState();
        _commandOutbox = commandOutbox;
        _workerId = string.IsNullOrWhiteSpace(workerId) ? Environment.MachineName + ":scheduler" : workerId.Trim();
        _outboxLease = outboxLease ?? DefaultOutboxLease;
        _persistenceStore = persistenceStore;
        _resourceLockStore = resourceLockStore ?? persistenceStore as IResourceLockStore;
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

                return PersistTaskStateAsync(request, cancellationToken);
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
        }

        request.Task.SetDispatchContext(PersistedDispatchContext.Serialize(request));
        if (string.IsNullOrWhiteSpace(request.Task.WorkflowKind))
        {
            request.Task.SetWorkflowContext(
                request.OperationKind.ToString(),
                request.Task.TaskNumber,
                request.Task.DispatchContextJson!);
            request.Task.MarkWorkflowRecovered();
        }
        return PersistTaskStateAsync(request, cancellationToken);
    }

    public async Task<TaskDispatchResult?> DispatchNextAsync(CancellationToken cancellationToken = default)
    {
        TaskDispatchRequest? request;
        lock (_gate)
        {
            request = _state.Requests.Values
                .Where(candidate => candidate.Task.State == TaskState.Queued
                    && candidate.Task.WorkflowRecoveryStatus != WorkflowRecoveryStatus.BlockedMissingBusinessState
                    && !_state.ActiveDevices.Contains(candidate.DeviceTask.DeviceId))
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

        if (_resourceLockStore is not null)
        {
            try
            {
                var lease = await _resourceLockStore.AcquireResourceLockAsync(
                    "Device", request.DeviceTask.DeviceId, request.Task.TaskNumber,
                    DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2), cancellationToken: cancellationToken);
                lock (_gate) _state.DeviceLeases[request.Task.TaskNumber] = lease;
            }
            catch (InvalidOperationException)
            {
                lock (_gate)
                {
                    request.Task.TransitionTo(TaskState.Queued, "scheduler", "device is leased by another worker", "DEVICE_LEASE_BUSY");
                    _state.ActiveDevices.Remove(request.DeviceTask.DeviceId);
                }
                await PersistTaskStateAsync(request, CancellationToken.None);
                return null;
            }
        }

        // Durable state must be visible before any device call can occur.
        await PersistTaskStateAsync(request, cancellationToken);

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

            await PersistTaskStateAsync(request, CancellationToken.None);

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

                await PersistTaskStateAsync(request, CancellationToken.None);
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

            await PersistTaskStateAsync(request, CancellationToken.None);

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

            await PersistTaskStateAsync(request, CancellationToken.None);
            return new TaskDispatchResult(
                request,
                DeviceOperationStatus.PhysicalStateUnknown,
                request.DeviceTaskNumber,
                "DEVICE_CALL_UNKNOWN",
                exception.Message);
        }

        TaskDispatchResult dispatchResult;
        var markPublished = false;
        var releaseDeviceLease = false;
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
                    releaseDeviceLease = true;
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
                        releaseDeviceLease = true;
                        nextAttemptAt = DateTimeOffset.UtcNow;
                    }
                    else
                    {
                        request.Task.TransitionTo(TaskState.Failed, "scheduler", "dispatch failed", result.ErrorCode);
                    }

                    _state.ActiveDevices.Remove(request.DeviceTask.DeviceId);
                    if (request.Task.State is TaskState.Failed) releaseDeviceLease = true;
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

        if (releaseDeviceLease)
            await ReleaseDeviceLeaseAsync(request);

        await PersistTaskStateAsync(request, CancellationToken.None);
        return dispatchResult;
    }

    internal async Task<bool> ApplyObservationAsync(TaskDispatchRequest request, DeviceResultObservation observation)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(observation);
        var releaseDeviceLease = false;
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
                return false;
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
                    releaseDeviceLease = true;
                    break;
                case DeviceOperationStatus.Failed:
                    if (request.Task.State is TaskState.SentToPlc or TaskState.Executing)
                    {
                        request.Task.TransitionTo(TaskState.Failed, "device-observer", "device failed");
                    }

                    _state.ActiveDevices.Remove(request.DeviceTask.DeviceId);
                    releaseDeviceLease = true;
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

        }

        if (releaseDeviceLease)
            await ReleaseDeviceLeaseAsync(request);
        await PersistTaskStateAsync(request, CancellationToken.None);
        return true;
    }

    private async Task PersistTaskStateAsync(TaskDispatchRequest request, CancellationToken cancellationToken)
    {
        if (_persistenceStore is null)
        {
            return;
        }

        request.Task.SetDispatchContext(PersistedDispatchContext.Serialize(request));

        var persisted = await _persistenceStore.GetTaskAsync(request.Task.TaskNumber, cancellationToken);
        if (persisted is null)
        {
            await _persistenceStore.CreateTaskAsync(request.Task, cancellationToken);
            persisted = await _persistenceStore.GetTaskAsync(request.Task.TaskNumber, cancellationToken);
        }

        if (persisted is null)
        {
            return;
        }

        if (persisted.Version > request.Task.Version
            || (persisted.Version == request.Task.Version && persisted.State != request.Task.State))
        {
            throw new InvalidOperationException($"Task '{request.Task.TaskNumber}' persistence conflict at version {request.Task.Version}.");
        }

        if (persisted.Version == request.Task.Version)
        {
            await _persistenceStore.UpdateDispatchContextAsync(request.Task.TaskNumber, request.Task.DispatchContextJson!, cancellationToken);
            return;
        }

        foreach (var history in request.Task.StateHistory.Where(x => x.Version > persisted.Version).OrderBy(x => x.Version))
        {
            persisted = await _persistenceStore.TransitionTaskAsync(
                request.Task.TaskNumber,
                persisted.Version,
                history.ToState,
                history.Operator,
                history.Reason,
                history.ErrorCode,
                history.OccurredAt,
                cancellationToken);
        }
        await _persistenceStore.UpdateDispatchContextAsync(request.Task.TaskNumber, request.Task.DispatchContextJson!, cancellationToken);
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

                await PersistTaskStateAsync(request, cancellationToken);

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
                await PersistTaskStateAsync(request, cancellationToken);
            }
        }
    }

    /// <summary>Marks durable in-flight work unknown when a restarted process has no safe device command payload to replay.</summary>
    public async Task RecoverPersistedInFlightAsync(CancellationToken cancellationToken = default)
    {
        if (_persistenceStore is null)
        {
            return;
        }

        var tasks = await _persistenceStore.GetTasksByStatesAsync(
            [TaskState.Queued, TaskState.Dispatching, TaskState.SentToPlc, TaskState.Executing], cancellationToken);
        foreach (var task in tasks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PersistedDispatchContext? context = null;
            if (!string.IsNullOrWhiteSpace(task.DispatchContextJson))
            {
                try
                {
                    context = JsonSerializer.Deserialize<PersistedDispatchContext>(task.DispatchContextJson);
                    if (context is not null)
                    {
                        lock (_gate)
                        {
                            if (!_state.Requests.ContainsKey(task.TaskNumber))
                                _state.Requests[task.TaskNumber] = context.ToRequest(task);
                            if (task.State is TaskState.Dispatching or TaskState.SentToPlc or TaskState.Executing)
                                _state.ActiveDevices.Add(context.DeviceId);
                        }
                    }
                }
                catch (JsonException)
                {
                    await _persistenceStore.MarkWorkflowRecoveryBlockedAsync(task.TaskNumber, "DISPATCH_CONTEXT_INVALID", cancellationToken);
                    continue;
                }
            }
            if (task.WorkflowRecoveryStatus == WorkflowRecoveryStatus.BlockedMissingBusinessState)
                continue;
            if (context is null && task.State == TaskState.Queued)
            {
                await _persistenceStore.MarkWorkflowRecoveryBlockedAsync(task.TaskNumber, "DISPATCH_CONTEXT_MISSING", cancellationToken);
                continue;
            }
            if (task.State == TaskState.Queued)
            {
                // Queued work has no device side effect; reconstructed requests are
                // intentionally left eligible for DispatchNextAsync.
                continue;
            }
            if (task.State is TaskState.Dispatching or TaskState.SentToPlc or TaskState.Executing)
            {
                // A persisted device task number is sufficient to reconcile with
                // the gateway after restart. Keep the in-flight state intact so
                // RecoverInFlightAsync can query it instead of declaring it
                // unknown before the device has been consulted.
                if (context is not null && !string.IsNullOrWhiteSpace(context.DeviceTaskNumber))
                {
                    continue;
                }
                var timedOut = await _persistenceStore.TransitionTaskAsync(
                    task.TaskNumber, task.Version, TaskState.TimedOut,
                    "worker", "restart cannot safely reconstruct device command", "RESTART_REQUIRES_RECONCILIATION", cancellationToken: cancellationToken);
                await _persistenceStore.TransitionTaskAsync(
                    task.TaskNumber, timedOut.Version, TaskState.PhysicalStateUnknown,
                    "worker", "physical result requires reconciliation", "PHYSICAL_UNKNOWN", cancellationToken: cancellationToken);
            }
        }
    }

    public Task<ResourceLock> AcquireResourceLockAsync(string resourceType, string resourceId, string ownerTaskNumber, DateTimeOffset acquiredAt, TimeSpan leaseDuration, Guid? lockToken = null, CancellationToken cancellationToken = default)
        => (_resourceLockStore ?? throw new InvalidOperationException("A resource lock store is required for persistent scheduling."))
            .AcquireResourceLockAsync(resourceType, resourceId, ownerTaskNumber, acquiredAt, leaseDuration, lockToken, cancellationToken);

    private async Task ReleaseDeviceLeaseAsync(TaskDispatchRequest request)
    {
        if (_resourceLockStore is null) return;
        ResourceLock? lease;
        lock (_gate)
        {
            _state.DeviceLeases.TryGetValue(request.Task.TaskNumber, out lease);
            _state.DeviceLeases.Remove(request.Task.TaskNumber);
        }
        if (lease is not null && lease.IsActive())
            await _resourceLockStore.ReleaseResourceLockAsync(lease.Id, request.Task.TaskNumber, lease.Version, DateTimeOffset.UtcNow, lease.LockToken, CancellationToken.None);
    }

    public Task<ResourceLock> RenewResourceLockAsync(Guid lockId, string ownerTaskNumber, int expectedVersion, DateTimeOffset renewedAt, TimeSpan leaseDuration, Guid lockToken, CancellationToken cancellationToken = default)
        => (_resourceLockStore ?? throw new InvalidOperationException("A resource lock store is required for persistent scheduling."))
            .RenewResourceLockAsync(lockId, ownerTaskNumber, expectedVersion, renewedAt, leaseDuration, lockToken, cancellationToken);

    public Task ReleaseResourceLockAsync(Guid lockId, string ownerTaskNumber, int expectedVersion, DateTimeOffset releasedAt, Guid lockToken, CancellationToken cancellationToken = default)
        => (_resourceLockStore ?? throw new InvalidOperationException("A resource lock store is required for persistent scheduling."))
            .ReleaseResourceLockAsync(lockId, ownerTaskNumber, expectedVersion, releasedAt, lockToken, cancellationToken);
}
