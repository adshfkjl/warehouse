using Warehouse.Wms.Application.Tasks;
using Warehouse.Wms.Domain.Devices;
using Warehouse.Wms.Domain.Outbound;
using Warehouse.Wms.Domain.Tasks;
using WmsTaskScheduler = Warehouse.Wms.Application.Tasks.TaskScheduler;
using System.Text.Json;

namespace Warehouse.Wms.Application.Outbound;

public sealed record OutboundLoadingPoint(
    Warehouse.Wms.Domain.MasterData.LoadingPoint LoadingPoint,
    bool IsOccupied,
    bool IsFaulted = false,
    bool IsDisabled = false,
    bool IsLocked = false);

public interface ILoadingPointCatalog
{
    Task<IReadOnlyList<OutboundLoadingPoint>> GetAsync(CancellationToken cancellationToken = default);
}

public sealed class InMemoryLoadingPointCatalog(IEnumerable<OutboundLoadingPoint> loadingPoints) : ILoadingPointCatalog
{
    private readonly IReadOnlyList<OutboundLoadingPoint> _loadingPoints = loadingPoints?.ToArray()
        ?? throw new ArgumentNullException(nameof(loadingPoints));

    public Task<IReadOnlyList<OutboundLoadingPoint>> GetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_loadingPoints);
    }
}

public sealed record OutboundTaskRequest(
    string TaskNumber,
    string DeviceId,
    Guid LoadingPointId,
    string ProtocolVersion = "v1",
    string? IdempotencyKey = null,
    int Priority = 0,
    int MaxAttempts = 1);

public sealed record OutboundTaskResult(
    OutboundAllocation Allocation,
    WarehouseTask Task,
    DeviceTask DeviceTask,
    IReadOnlyList<ResourceLock> ResourceLocks,
    TaskDispatchResult? DispatchResult,
    string ExpectedPalletCode,
    decimal ExpectedWeightKg);

public sealed class OutboundTaskService
{
    private readonly object _gate = new();
    private readonly WmsTaskScheduler _scheduler;
    private readonly OutboundAllocationService _allocations;
    private readonly IResourceLockStore? _resourceLockStore;
    private readonly ILoadingPointCatalog _loadingPointCatalog;
    private readonly IBusinessWorkflowStore? _workflowStore;
    private readonly Dictionary<string, int> _workflowVersions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OutboundTaskResult> _tasks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskDispatchRequest> _dispatchRequests = new(StringComparer.Ordinal);

    public OutboundTaskService(
        OutboundAllocationService allocations,
        WmsTaskScheduler scheduler,
        IEnumerable<OutboundLoadingPoint> loadingPoints,
        IResourceLockStore? resourceLockStore = null,
        IBusinessWorkflowStore? workflowStore = null)
    {
        _allocations = allocations ?? throw new ArgumentNullException(nameof(allocations));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _loadingPointCatalog = new InMemoryLoadingPointCatalog(loadingPoints);
        _resourceLockStore = resourceLockStore;
        _workflowStore = workflowStore;
    }

    public OutboundTaskService(
        OutboundAllocationService allocations,
        WmsTaskScheduler scheduler,
        ILoadingPointCatalog loadingPointCatalog,
        IResourceLockStore? resourceLockStore = null,
        IBusinessWorkflowStore? workflowStore = null)
    {
        _allocations = allocations ?? throw new ArgumentNullException(nameof(allocations));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _loadingPointCatalog = loadingPointCatalog ?? throw new ArgumentNullException(nameof(loadingPointCatalog));
        _resourceLockStore = resourceLockStore;
        _workflowStore = workflowStore;
    }

    public OutboundTaskResult Get(string taskNumber)
    {
        var normalized = Require(taskNumber, nameof(taskNumber));
        lock (_gate)
        {
            return _tasks.TryGetValue(normalized, out var result)
                ? result
                : throw new KeyNotFoundException($"Outbound task '{normalized}' was not found.");
        }
    }

    public async Task<OutboundTaskResult> SubmitAsync(
        OutboundAllocation allocation,
        OutboundTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(allocation);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var taskNumber = Require(request.TaskNumber, nameof(request.TaskNumber));
        var idempotencyKey = Require(request.IdempotencyKey ?? $"outbound:{taskNumber}", nameof(request.IdempotencyKey));
        lock (_gate)
        {
            if (_tasks.TryGetValue(taskNumber, out var existing))
            {
                if (!string.Equals(existing.DeviceTask.IdempotencyKey, idempotencyKey, StringComparison.Ordinal))
                    throw new InvalidOperationException("The outbound task number is already used by another command.");
                return existing;
            }
        }

        var loadingPoint = (await _loadingPointCatalog.GetAsync(cancellationToken)).FirstOrDefault(item => item.LoadingPoint.Id == request.LoadingPointId)
            ?? throw new KeyNotFoundException($"Loading point '{request.LoadingPointId}' was not found.");
        if (loadingPoint.IsOccupied || loadingPoint.IsFaulted || loadingPoint.IsDisabled || loadingPoint.IsLocked || loadingPoint.LoadingPoint.IsDisabled)
            throw new InvalidOperationException($"Loading point '{loadingPoint.LoadingPoint.Code}' is not available for outbound.");

        var task = new WarehouseTask(taskNumber, "Outbound");
        var deviceTask = new DeviceTask(
            idempotencyKey,
            taskNumber,
            Require(request.DeviceId, nameof(request.DeviceId)),
            allocation.Source.LocationId?.ToString("D"),
            loadingPoint.LoadingPoint.Code,
            loadingPoint.LoadingPoint.Code,
            Require(request.ProtocolVersion, nameof(request.ProtocolVersion)));
        var dispatchRequest = new TaskDispatchRequest(task, deviceTask, DeviceOperationKind.Outbound, request.Priority, request.MaxAttempts);
        var now = DateTimeOffset.UtcNow;
        ResourceLock loadingLock = _resourceLockStore is null
            ? new ResourceLock("LoadingPoint", loadingPoint.LoadingPoint.Id.ToString("D"), taskNumber, now, TimeSpan.FromMinutes(15))
            : await _resourceLockStore.AcquireResourceLockAsync("LoadingPoint", loadingPoint.LoadingPoint.Id.ToString("D"), taskNumber, now, TimeSpan.FromMinutes(15), cancellationToken: cancellationToken);
        var locks = allocation.ResourceLocks.Concat([loadingLock]).ToArray();
        try
        {
            await _scheduler.EnqueueAsync(dispatchRequest, cancellationToken);
            var result = new OutboundTaskResult(
                allocation,
                task,
                deviceTask,
                locks,
                null,
                allocation.Source.PalletId?.ToString("D") ?? allocation.Source.Key,
                allocation.Source.Quantity == 0m ? 0m : allocation.Source.WeightKg / allocation.Source.Quantity * allocation.Quantity);
            lock (_gate)
            {
                _tasks.Add(taskNumber, result);
                _dispatchRequests.Add(taskNumber, dispatchRequest);
            }
            Persist(taskNumber, result);
            if (allocation.Order.State == OutboundState.Locked)
                allocation.Order.TransitionTo(OutboundState.Picking);

            var dispatch = await _scheduler.DispatchNextAsync(cancellationToken);
            if (dispatch is not null && ReferenceEquals(dispatch.Request.Task, task))
            {
                var submitted = result with { DispatchResult = dispatch };
                lock (_gate) _tasks[taskNumber] = submitted;
                Persist(taskNumber, submitted);
                return submitted;
            }

            return result;
        }
        catch
        {
            if (_resourceLockStore is null)
                loadingLock.Release(taskNumber, loadingLock.Version, DateTimeOffset.UtcNow, loadingLock.LockToken);
            else if (loadingLock.IsActive())
                await _resourceLockStore.ReleaseResourceLockAsync(loadingLock.Id, taskNumber, loadingLock.Version, DateTimeOffset.UtcNow, loadingLock.LockToken, CancellationToken.None);
            throw;
        }
    }

    public async Task<OutboundTaskResult> ApplyDeviceResultAsync(
        string taskNumber,
        DeviceOperationStatus status,
        CancellationToken cancellationToken = default)
    {
        var result = Get(taskNumber);
        TaskDispatchRequest request;
        lock (_gate) request = _dispatchRequests[taskNumber];
        var deviceTaskNumber = request.DeviceTaskNumber
            ?? result.DispatchResult?.DeviceTaskNumber;
        if (string.IsNullOrWhiteSpace(deviceTaskNumber))
        {
            // Without a device-issued task identity the result cannot be safely
            // correlated after a timeout or restart. Preserve the task and let
            // the caller route it to physical-state reconciliation.
            if (request.Task.State is TaskState.SentToPlc or TaskState.Executing)
            {
                request.Task.TransitionTo(TaskState.TimedOut, "outbound-recovery", "设备结果缺少任务号，无法关联", "DEVICE_TASK_NUMBER_MISSING");
                request.Task.TransitionTo(TaskState.PhysicalStateUnknown, "outbound-recovery", "需要人工核对物理结果", "PHYSICAL_UNKNOWN");
            }
            Persist(taskNumber, result);
            return Get(taskNumber);
        }
        var observation = new DeviceResultObservation(
            deviceTaskNumber,
            1,
            status,
            DeviceObservationSource.Polling,
            DateTimeOffset.UtcNow);
        await _scheduler.ApplyObservationAsync(request, observation);
        var updated = Get(taskNumber);
        Persist(taskNumber, updated);
        return updated;
    }

    public async Task ReleaseLoadingPointAsync(OutboundTaskResult result)
    {
        var loadingLock = result.ResourceLocks.FirstOrDefault(item => item.ResourceType == "LoadingPoint");
        if (loadingLock is not null && loadingLock.IsActive())
        {
            var now = DateTimeOffset.UtcNow;
            if (_resourceLockStore is null)
                loadingLock.Release(result.Task.TaskNumber, loadingLock.Version, now, loadingLock.LockToken);
            else
                await _resourceLockStore.ReleaseResourceLockAsync(loadingLock.Id, result.Task.TaskNumber, loadingLock.Version, now, loadingLock.LockToken);
        }
    }

    public async Task ReleaseAllocationResourcesAsync(OutboundTaskResult result)
    {
        foreach (var resourceLock in result.Allocation.ResourceLocks)
        {
            if (resourceLock.IsActive())
            {
                var now = DateTimeOffset.UtcNow;
                if (_resourceLockStore is null)
                    resourceLock.Release(result.Allocation.Order.OrderNumber, resourceLock.Version, now, resourceLock.LockToken);
                else
                    await _resourceLockStore.ReleaseResourceLockAsync(resourceLock.Id, result.Allocation.Order.OrderNumber, resourceLock.Version, now, resourceLock.LockToken);
            }
        }
    }

    public void PersistCurrentState(string taskNumber)
    {
        var result = Get(taskNumber);
        Persist(taskNumber, result);
    }

    private static string Require(string? value, string parameterName)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty value is required.", parameterName)
            : value.Trim();

    private void Persist(string taskNumber, OutboundTaskResult result)
    {
        if (_workflowStore is null) return;
        var key = taskNumber.Trim();
        var version = _workflowVersions.TryGetValue(key, out var current) ? current : 0;
        TaskDispatchRequest? dispatchRequest;
        lock (_gate) dispatchRequest = _dispatchRequests.TryGetValue(key, out var persistedRequest) ? persistedRequest : null;
        var state = new OutboundTaskState(result.Task.State, result.Allocation.Order.State, result.ExpectedPalletCode, result.ExpectedWeightKg, result.DeviceTask.IdempotencyKey, result.DispatchResult?.DeviceTaskNumber ?? dispatchRequest?.DeviceTaskNumber, result.Allocation.Order.OrderNumber, result.Task.TaskNumber,
            result.Allocation.Line.Id, result.Allocation.Line.MaterialId, result.Allocation.Line.RequestedQuantity, result.Allocation.Source.PalletId, result.Allocation.Source.LocationId,
            result.Allocation.Source.BatchNumber, result.Allocation.Quantity, result.Allocation.Source.WeightKg, result.DeviceTask.DeviceId, result.DeviceTask.ProtocolVersion, result.DeviceTask.SourceLocation, result.DeviceTask.DestinationLocation, result.DeviceTask.LoadingPoint, result.Task.TaskType,
            result.Allocation.IdempotencyKey, dispatchRequest?.Priority ?? 0, dispatchRequest?.MaxAttempts ?? 1, dispatchRequest?.AttemptCount ?? 0);
        var json = JsonSerializer.Serialize(state);
        _workflowStore.SaveAsync(new BusinessWorkflowSnapshot("OutboundTask", key, version + 1, result.Task.State.ToString(), json, DateTimeOffset.UtcNow, key), version, "出库任务状态保存", "system").GetAwaiter().GetResult();
        _workflowVersions[key] = version + 1;
        _workflowStore.RegisterIdempotencyAsync("outbound-task", key, $"task:{result.DeviceTask.IdempotencyKey}", "OutboundTask", key).GetAwaiter().GetResult();
    }

    private sealed record OutboundTaskState(TaskState TaskState, OutboundState OrderState, string ExpectedPalletCode, decimal ExpectedWeightKg, string IdempotencyKey, string? DeviceTaskNumber, string OrderNumber, string TaskNumber,
        Guid LineId, Guid MaterialId, decimal RequestedQuantity, Guid? PalletId, Guid? LocationId, string? BatchNumber, decimal Quantity, decimal SourceWeightKg,
        string DeviceId, string ProtocolVersion, string? SourceLocation, string? DestinationLocation, string? LoadingPoint, string TaskType,
        string? AllocationIdempotencyKey, int Priority, int MaxAttempts, int AttemptCount);

    /// <summary>
    /// Rehydrates outbound tasks, allocation references and active locks before
    /// review recovery. No PLC command is submitted during this operation.
    /// </summary>
    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        if (_workflowStore is null) return;
        var snapshots = await _workflowStore.GetByTypeAsync("OutboundTask", cancellationToken);
        var durableLocks = _resourceLockStore is null
            ? Array.Empty<ResourceLock>()
            : (await _resourceLockStore.GetActiveResourceLocksAsync(cancellationToken: cancellationToken)).ToArray();
        foreach (var snapshot in snapshots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OutboundTaskState? state;
            try { state = JsonSerializer.Deserialize<OutboundTaskState>(snapshot.SnapshotJson); }
            catch (JsonException) { continue; }
            if (state is null) continue;
            lock (_gate)
            {
                if (_tasks.ContainsKey(state.TaskNumber)) continue;
            }

            var locks = durableLocks.Where(item =>
                string.Equals(item.OwnerTaskNumber, state.TaskNumber, StringComparison.Ordinal)
                || string.Equals(item.OwnerTaskNumber, state.OrderNumber, StringComparison.Ordinal)).ToArray();
            var allocation = _allocations.Restore(state.OrderNumber, state.LineId, state.MaterialId, state.PalletId, state.LocationId, state.BatchNumber, state.RequestedQuantity, state.Quantity, locks, state.AllocationIdempotencyKey);
            RestoreOrderState(allocation.Order, state.OrderState);
            var task = new WarehouseTask(state.TaskNumber, state.TaskType);
            RestoreTaskState(task, state.TaskState);
            var deviceTask = new DeviceTask(state.IdempotencyKey, state.TaskNumber, state.DeviceId, state.SourceLocation, state.DestinationLocation, state.LoadingPoint, state.ProtocolVersion);
            var request = new TaskDispatchRequest(task, deviceTask, DeviceOperationKind.Outbound, state.Priority, state.MaxAttempts) { AttemptCount = state.AttemptCount, DeviceTaskNumber = state.DeviceTaskNumber };
            TaskDispatchResult? restoredDispatch = string.IsNullOrWhiteSpace(state.DeviceTaskNumber)
                ? null
                : new TaskDispatchResult(request, DeviceOperationStatus.Accepted, state.DeviceTaskNumber);
            var result = new OutboundTaskResult(allocation, task, deviceTask, locks, restoredDispatch, state.ExpectedPalletCode, state.ExpectedWeightKg);
            lock (_gate)
            {
                _tasks[state.TaskNumber] = result;
                _dispatchRequests[state.TaskNumber] = request;
                _workflowVersions[state.TaskNumber] = snapshot.Version;
            }
        }
    }

    private static void RestoreTaskState(WarehouseTask task, TaskState target)
    {
        if (target == task.State) return;
        var graph = new Dictionary<TaskState, TaskState[]>
        {
            [TaskState.Created] = [TaskState.Allocated, TaskState.Canceled],
            [TaskState.Allocated] = [TaskState.Queued, TaskState.Canceled],
            [TaskState.Queued] = [TaskState.Dispatching, TaskState.Failed, TaskState.Canceled],
            [TaskState.Dispatching] = [TaskState.Queued, TaskState.SentToPlc, TaskState.Failed, TaskState.TimedOut],
            [TaskState.SentToPlc] = [TaskState.Executing, TaskState.Failed, TaskState.TimedOut, TaskState.CancelRequested, TaskState.StopRequested, TaskState.PhysicalStateUnknown],
            [TaskState.Executing] = [TaskState.Succeeded, TaskState.Failed, TaskState.TimedOut, TaskState.CancelRequested, TaskState.StopRequested, TaskState.PhysicalStateUnknown],
            [TaskState.Failed] = [TaskState.PhysicalStateUnknown, TaskState.ManualIntervention],
            [TaskState.TimedOut] = [TaskState.PhysicalStateUnknown, TaskState.Failed, TaskState.ManualIntervention],
            [TaskState.CancelRequested] = [TaskState.StopRequested],
            [TaskState.StopRequested] = [TaskState.StopConfirmed, TaskState.StopFailed, TaskState.PhysicalStateUnknown],
            [TaskState.StopConfirmed] = [TaskState.Canceled, TaskState.ManualIntervention],
            [TaskState.StopFailed] = [TaskState.StopRequested, TaskState.PhysicalStateUnknown, TaskState.ManualIntervention],
            [TaskState.PhysicalStateUnknown] = [TaskState.Executing, TaskState.Succeeded, TaskState.Failed, TaskState.ManualIntervention]
        };
        var queue = new Queue<(TaskState State, List<TaskState> Path)>();
        queue.Enqueue((task.State, []));
        var visited = new HashSet<TaskState> { task.State };
        List<TaskState>? found = null;
        while (queue.Count > 0 && found is null)
        {
            var (current, path) = queue.Dequeue();
            if (!graph.TryGetValue(current, out var nextStates)) continue;
            foreach (var next in nextStates)
            {
                if (!visited.Add(next)) continue;
                var nextPath = new List<TaskState>(path) { next };
                if (next == target) { found = nextPath; break; }
                queue.Enqueue((next, nextPath));
            }
        }
        if (found is null) throw new InvalidOperationException($"Outbound task '{task.TaskNumber}' state '{target}' cannot be rehydrated.");
        foreach (var state in found) task.TransitionTo(state, "recovery", "从出库业务快照恢复");
    }

    private static void RestoreOrderState(OutboundOrder order, OutboundState target)
    {
        while (order.State != target)
        {
            if (order.State == OutboundState.Draft) order.TransitionTo(OutboundState.Allocated);
            else if (order.State == OutboundState.Allocated) order.TransitionTo(OutboundState.Locked);
            else if (order.State == OutboundState.Locked) order.TransitionTo(OutboundState.Picking);
            else if (order.State == OutboundState.Picking && target is OutboundState.AwaitingReview) order.TransitionTo(OutboundState.AwaitingReview);
            else if (order.State == OutboundState.Picking && target is OutboundState.Completed) order.TransitionTo(OutboundState.AwaitingReview);
            else if (order.State == OutboundState.Picking && target is OutboundState.Exception) order.TransitionTo(OutboundState.Exception);
            else if (order.State == OutboundState.AwaitingReview && target is OutboundState.Completed) order.TransitionTo(OutboundState.Completed);
            else if (order.State == OutboundState.AwaitingReview && target is OutboundState.Exception) order.TransitionTo(OutboundState.Exception);
            else if (order.State == OutboundState.Exception && target is OutboundState.Allocated) order.TransitionTo(OutboundState.Allocated);
            else if (order.State == OutboundState.Exception && target is OutboundState.Canceled) order.TransitionTo(OutboundState.Canceled);
            else if (order.State is OutboundState.Draft or OutboundState.Allocated or OutboundState.Locked) order.TransitionTo(OutboundState.Canceled);
            else throw new InvalidOperationException($"Outbound order '{order.OrderNumber}' state '{order.State}' cannot be restored to '{target}'.");
        }
    }

}
