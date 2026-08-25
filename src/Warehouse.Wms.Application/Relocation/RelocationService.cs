using Warehouse.Wms.Application.Devices;
using Warehouse.Wms.Application.Inventory;
using Warehouse.Wms.Application.Tasks;
using Warehouse.Wms.Domain.Devices;
using Warehouse.Wms.Domain.Inventory;
using Warehouse.Wms.Domain.Relocation;
using Warehouse.Wms.Domain.Tasks;
using WmsTaskScheduler = Warehouse.Wms.Application.Tasks.TaskScheduler;
using System.Text.Json;

namespace Warehouse.Wms.Application.Relocation;

public sealed record RelocationRequest(
    string IdempotencyKey,
    Guid MaterialId,
    Guid PalletId,
    Guid SourceLocationId,
    Guid DestinationLocationId,
    decimal Quantity,
    decimal WeightKg,
    string DeviceId,
    string? BatchNumber = null,
    string ProtocolVersion = "v1",
    int Priority = 0,
    int MaxAttempts = 1,
    string? SourceDeviceId = null,
    string? DestinationDeviceId = null);

public enum RelocationStatus
{
    Queued,
    Executing,
    Completed,
    AlreadyCompleted,
    Failed,
    PhysicalStateUnknown,
    Exception
}

public sealed record RelocationResult(
    string IdempotencyKey,
    RelocationStatus Status,
    RelocationOrder Order,
    WarehouseTask Task,
    IReadOnlyList<ResourceLock> ResourceLocks,
    InventoryTransaction? InventoryTransaction = null);

public sealed class RelocationService
{
    private readonly object _gate = new();
    private readonly InventoryService _inventory;
    private readonly WmsTaskScheduler _scheduler;
    private readonly IResourceLockStore? _resourceLockStore;
    private readonly IBusinessWorkflowStore? _workflowStore;
    private readonly Dictionary<string, RelocationResult> _results = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskDispatchRequest> _dispatches = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _workflowVersions = new(StringComparer.Ordinal);
    private bool _restoring;

    public RelocationService(InventoryService inventory, WmsTaskScheduler scheduler, IResourceLockStore? resourceLockStore = null, IBusinessWorkflowStore? workflowStore = null)
    {
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _resourceLockStore = resourceLockStore;
        _workflowStore = workflowStore;
    }

    public RelocationResult Get(string idempotencyKey)
    {
        var key = Require(idempotencyKey, nameof(idempotencyKey));
        lock (_gate)
        {
            return _results.TryGetValue(key, out var result)
                ? result
                : throw new KeyNotFoundException($"Relocation '{key}' was not found.");
        }
    }

    public async Task<RelocationResult> SubmitAsync(RelocationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var key = Require(request.IdempotencyKey, nameof(request.IdempotencyKey));
        var deviceId = Require(request.DeviceId, nameof(request.DeviceId));
        lock (_gate)
        {
            if (_results.TryGetValue(key, out var existing)) return existing;
        }

        ValidateRequest(request);
        var source = _inventory.GetBalance(request.MaterialId, request.PalletId, request.SourceLocationId, request.BatchNumber)
            ?? (request.BatchNumber is null
                ? _inventory.GetBalances()
                    .Where(item => item.MaterialId == request.MaterialId
                        && item.PalletId == request.PalletId
                        && item.LocationId == request.SourceLocationId
                        && item.Status == InventoryStatus.Available
                        && item.Quantity >= request.Quantity
                        && item.WeightKg >= request.WeightKg)
                    .OrderBy(item => item.BatchNumber, StringComparer.Ordinal)
                    .FirstOrDefault()
                : null)
            ?? throw new InvalidOperationException("The source location has no matching inventory.");
        if (source.Status != InventoryStatus.Available || source.Quantity < request.Quantity || source.WeightKg < request.WeightKg)
            throw new InvalidOperationException("The source inventory is unavailable or insufficient.");
        var destination = _inventory.GetBalance(request.MaterialId, request.PalletId, request.DestinationLocationId, request.BatchNumber);
        if (destination is not null && destination.Quantity > 0m)
            throw new InvalidOperationException("The destination location is occupied.");

        var order = new RelocationOrder(
            $"REL-{key}", request.MaterialId, request.PalletId, request.SourceLocationId,
            request.DestinationLocationId, request.Quantity, request.WeightKg, source.BatchNumber);
        var now = DateTimeOffset.UtcNow;
        var resources = new[]
        {
            ("Inventory", source.Key),
            ("Pallet", request.PalletId.ToString("D")),
            ("Location", request.SourceLocationId.ToString("D")),
            ("Location", request.DestinationLocationId.ToString("D")),
            ("Device", deviceId)
        };
        if (_resourceLockStore is not null)
        {
            var activeLocks = await _resourceLockStore.GetActiveResourceLocksAsync(now, cancellationToken);
            if (resources.Any(item => activeLocks.Any(existing => existing.ResourceKey == ResourceLock.BuildResourceKey(item.Item1, item.Item2))))
                throw new InvalidOperationException("One or more relocation resources are already locked.");
        }
        else
        {
            lock (_gate)
            {
                var activeKeys = _results.Values
                    .SelectMany(item => item.ResourceLocks)
                    .Where(item => item.IsActive(now))
                    .Select(item => item.ResourceKey)
                    .ToHashSet(StringComparer.Ordinal);
                if (resources.Any(item => activeKeys.Contains(ResourceLock.BuildResourceKey(item.Item1, item.Item2))))
                    throw new InvalidOperationException("One or more relocation resources are already locked.");
            }
        }
        ResourceLock[] locks;
        if (_resourceLockStore is not null)
        {
            var acquired = new List<ResourceLock>(resources.Length);
            try
            {
                foreach (var resource in resources)
                {
                    acquired.Add(await _resourceLockStore.AcquireResourceLockAsync(
                        resource.Item1, resource.Item2, order.OrderNumber, now, TimeSpan.FromMinutes(15), cancellationToken: cancellationToken));
                }

                locks = acquired.ToArray();
            }
            catch
            {
                await ReleaseAsync(acquired, order.OrderNumber);
                throw;
            }
        }
        else
        {
            locks = resources.Select(item => new ResourceLock(item.Item1, item.Item2, order.OrderNumber, now, TimeSpan.FromMinutes(15))).ToArray();
        }
        var task = new WarehouseTask(order.OrderNumber, "Relocation");
        var deviceTask = new DeviceTask(key, task.TaskNumber, deviceId,
            request.SourceLocationId.ToString("D"), request.DestinationLocationId.ToString("D"), null,
            Require(request.ProtocolVersion, nameof(request.ProtocolVersion)));
        var dispatch = new TaskDispatchRequest(task, deviceTask, DeviceOperationKind.Transfer, request.Priority, request.MaxAttempts);
        try
        {
            await _scheduler.EnqueueAsync(dispatch, cancellationToken);
            order.TransitionTo(RelocationState.Allocated);
            order.TransitionTo(RelocationState.Queued);
            lock (_gate)
            {
                _dispatches.Add(key, dispatch);
                _results.Add(key, new RelocationResult(key, RelocationStatus.Queued, order, task, locks));
            }
            var sent = await _scheduler.DispatchNextAsync(cancellationToken);
            if (sent is not null && ReferenceEquals(sent.Request.Task, task))
            {
                if (sent.Status == DeviceOperationStatus.Succeeded)
                    return await ProcessResultAsync(key, DeviceOperationStatus.Succeeded, cancellationToken);
                var status = sent.Status switch
                {
                    DeviceOperationStatus.Accepted or DeviceOperationStatus.Executing => RelocationStatus.Executing,
                    DeviceOperationStatus.Failed or DeviceOperationStatus.Offline => RelocationStatus.Failed,
                    _ => RelocationStatus.PhysicalStateUnknown
                };
                lock (_gate) _results[key] = _results[key] with { Status = status };
            }
            Persist(Get(key));
            return Get(key);
        }
        catch
        {
            await ReleaseAsync(locks, order.OrderNumber);
            throw;
        }
    }

    public async Task<RelocationResult> CompleteAsync(string idempotencyKey, CancellationToken cancellationToken = default)
        => await ProcessResultAsync(idempotencyKey, DeviceOperationStatus.Succeeded, cancellationToken);

    public async Task<RelocationResult> ProcessResultAsync(string idempotencyKey, DeviceOperationStatus status, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = Require(idempotencyKey, nameof(idempotencyKey));
        var current = Get(key);
        if (current.Status == RelocationStatus.Completed)
            return current with { Status = RelocationStatus.AlreadyCompleted };
        var dispatch = _dispatches[key];

        if (status is DeviceOperationStatus.Failed or DeviceOperationStatus.Offline)
        {
            current.Order.TransitionTo(RelocationState.Failed);
            var failed = Store(current with { Status = RelocationStatus.Failed });
            Persist(failed);
            return failed;
        }
        if (status is DeviceOperationStatus.PhysicalStateUnknown or DeviceOperationStatus.Unknown or DeviceOperationStatus.TimedOut)
        {
            if (current.Task.State is TaskState.SentToPlc or TaskState.Executing)
            {
                current.Task.TransitionTo(TaskState.TimedOut, "relocation", "设备结果无法确认");
                current.Task.TransitionTo(TaskState.PhysicalStateUnknown, "relocation", "物理结果未知");
            }
            current.Order.TransitionTo(RelocationState.PhysicalStateUnknown);
            var unknown = Store(current with { Status = RelocationStatus.PhysicalStateUnknown });
            Persist(unknown);
            return unknown;
        }
        if (status != DeviceOperationStatus.Succeeded)
            throw new ArgumentException($"Unsupported relocation result '{status}'.", nameof(status));

        if (current.Task.State is TaskState.SentToPlc or TaskState.Executing)
        {
            var deviceTaskNumber = dispatch.DeviceTaskNumber;
            if (!string.IsNullOrWhiteSpace(deviceTaskNumber))
            {
                await _scheduler.ApplyObservationAsync(dispatch, new DeviceResultObservation(
                    deviceTaskNumber, 1, status, DeviceObservationSource.Polling, DateTimeOffset.UtcNow));
            }
        }
        if (current.Task.State != TaskState.Succeeded)
            throw new InvalidOperationException("The relocation task is not in a succeeded state.");
        var transaction = await _inventory.MoveAsync(
            current.Order.MaterialId, current.Order.PalletId, current.Order.SourceLocationId,
            current.Order.DestinationLocationId, current.Order.BatchNumber, current.Order.Quantity, current.Order.WeightKg,
            new InventoryOperationContext($"relocation:{key}:move", current.Order.OrderNumber, current.Task.TaskNumber, "relocation", "设备确认移库"),
            cancellationToken);
        current.Order.TransitionTo(RelocationState.Completed);
        await ReleaseAsync(current.ResourceLocks, current.Order.OrderNumber);
        var completed = Store(current with { Status = RelocationStatus.Completed, InventoryTransaction = transaction });
        Persist(completed);
        return completed;
    }

    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        if (_workflowStore is null) return;
        var snapshots = await _workflowStore.GetByTypeAsync("Relocation", cancellationToken);
        var activeLocks = _resourceLockStore is null
            ? Array.Empty<ResourceLock>()
            : (await _resourceLockStore.GetActiveResourceLocksAsync(cancellationToken: cancellationToken)).ToArray();
        lock (_gate) _restoring = true;
        try
        {
            foreach (var snapshot in snapshots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RelocationWorkflowState? state;
                try { state = JsonSerializer.Deserialize<RelocationWorkflowState>(snapshot.SnapshotJson); }
                catch (JsonException) { continue; }
                if (state is null || string.IsNullOrWhiteSpace(state.IdempotencyKey)) continue;
                lock (_gate) if (_results.ContainsKey(state.IdempotencyKey)) continue;
                try
                {
                    var order = new RelocationOrder(state.OrderNumber, state.MaterialId, state.PalletId, state.SourceLocationId, state.DestinationLocationId, state.Quantity, state.WeightKg, state.BatchNumber, state.OrderId);
                    order.RestoreState(state.OrderState);
                    var task = new WarehouseTask(state.TaskNumber, state.TaskType, state.TaskCreatedAt, state.TaskId);
                    RestoreTaskState(task, state.TaskState);
                    var device = new DeviceTask(state.IdempotencyKey, state.TaskNumber, state.DeviceId, state.SourceLocation, state.DestinationLocation, state.LoadingPoint, state.ProtocolVersion);
                    var dispatch = new TaskDispatchRequest(task, device, DeviceOperationKind.Transfer, state.Priority, state.MaxAttempts)
                    {
                        AttemptCount = state.AttemptCount,
                        DeviceTaskNumber = state.DeviceTaskNumber
                    };
                    var locks = activeLocks.Where(x => x.OwnerTaskNumber == state.TaskNumber || x.OwnerTaskNumber == state.OrderNumber).ToArray();
                    var result = new RelocationResult(state.IdempotencyKey, state.Status, order, task, locks);
                    lock (_gate)
                    {
                        _results[state.IdempotencyKey] = result;
                        _dispatches[state.IdempotencyKey] = dispatch;
                        _workflowVersions[state.IdempotencyKey] = snapshot.Version;
                    }
                }
                catch (InvalidOperationException) { /* Worker will block malformed business state. */ }
            }
        }
        finally { lock (_gate) _restoring = false; }
    }

    private void Persist(RelocationResult result)
    {
        if (_workflowStore is null || _restoring) return;
        var key = result.IdempotencyKey;
        var version = _workflowVersions.TryGetValue(key, out var current) ? current : 0;
        var dispatch = _dispatches.TryGetValue(key, out var request) ? request : null;
        if (dispatch is null) return;
        var state = new RelocationWorkflowState(
            result.Order.Id, result.Order.OrderNumber, result.Order.MaterialId, result.Order.PalletId,
            result.Order.SourceLocationId, result.Order.DestinationLocationId, result.Order.Quantity,
            result.Order.WeightKg, result.Order.BatchNumber, result.Order.State, result.Status,
            result.Task.Id, result.Task.TaskNumber, result.Task.TaskType, result.Task.State, result.Task.CreatedAt,
            dispatch.DeviceTask.IdempotencyKey, dispatch.DeviceTask.DeviceId, dispatch.DeviceTask.ProtocolVersion,
            dispatch.DeviceTask.SourceLocation, dispatch.DeviceTask.DestinationLocation, dispatch.DeviceTask.LoadingPoint,
            dispatch?.DeviceTaskNumber, dispatch?.Priority ?? 0, dispatch?.MaxAttempts ?? 1, dispatch?.AttemptCount ?? 0);
        var json = JsonSerializer.Serialize(state);
        _workflowStore.SaveAsync(new BusinessWorkflowSnapshot("Relocation", key, version + 1, result.Status.ToString(), json, DateTimeOffset.UtcNow, result.Task.TaskNumber), version, "移库业务状态保存", "system").GetAwaiter().GetResult();
        _workflowVersions[key] = version + 1;
        _workflowStore.RegisterIdempotencyAsync("relocation", key, $"relocation:{key}", "Relocation", key).GetAwaiter().GetResult();
    }

    private static void RestoreTaskState(WarehouseTask task, TaskState target)
    {
        var path = target switch
        {
            TaskState.Created => Array.Empty<TaskState>(),
            TaskState.Allocated => new[] { TaskState.Allocated },
            TaskState.Queued => new[] { TaskState.Allocated, TaskState.Queued },
            TaskState.Dispatching => new[] { TaskState.Allocated, TaskState.Queued, TaskState.Dispatching },
            TaskState.SentToPlc => new[] { TaskState.Allocated, TaskState.Queued, TaskState.Dispatching, TaskState.SentToPlc },
            TaskState.Executing => new[] { TaskState.Allocated, TaskState.Queued, TaskState.Dispatching, TaskState.SentToPlc, TaskState.Executing },
            TaskState.Succeeded => new[] { TaskState.Allocated, TaskState.Queued, TaskState.Dispatching, TaskState.SentToPlc, TaskState.Executing, TaskState.Succeeded },
            TaskState.Failed => new[] { TaskState.Allocated, TaskState.Queued, TaskState.Dispatching, TaskState.SentToPlc, TaskState.Failed },
            TaskState.TimedOut => new[] { TaskState.Allocated, TaskState.Queued, TaskState.Dispatching, TaskState.SentToPlc, TaskState.TimedOut },
            TaskState.PhysicalStateUnknown => new[] { TaskState.Allocated, TaskState.Queued, TaskState.Dispatching, TaskState.SentToPlc, TaskState.TimedOut, TaskState.PhysicalStateUnknown },
            _ => throw new InvalidOperationException($"Relocation task state '{target}' cannot be restored.")
        };
        foreach (var state in path) task.TransitionTo(state, "recovery", "从移库业务快照恢复");
    }

    private sealed record RelocationWorkflowState(Guid OrderId, string OrderNumber, Guid MaterialId, Guid PalletId, Guid SourceLocationId, Guid DestinationLocationId, decimal Quantity, decimal WeightKg, string? BatchNumber, RelocationState OrderState, RelocationStatus Status, Guid TaskId, string TaskNumber, string TaskType, TaskState TaskState, DateTimeOffset TaskCreatedAt, string IdempotencyKey, string DeviceId, string ProtocolVersion, string? SourceLocation, string? DestinationLocation, string? LoadingPoint, string? DeviceTaskNumber, int Priority, int MaxAttempts, int AttemptCount);

    private RelocationResult Store(RelocationResult result)
    {
        lock (_gate) _results[result.IdempotencyKey] = result;
        return result;
    }

    private async Task ReleaseAsync(IReadOnlyList<ResourceLock> locks, string owner)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var resourceLock in locks.Where(item => item.IsActive()))
        {
            if (_resourceLockStore is not null)
            {
                await _resourceLockStore.ReleaseResourceLockAsync(resourceLock.Id, owner, resourceLock.Version, now, resourceLock.LockToken);
            }
            else
            {
                resourceLock.Release(owner, resourceLock.Version, now, resourceLock.LockToken);
            }
        }
    }

    private static void ValidateRequest(RelocationRequest request)
    {
        if (request.MaterialId == Guid.Empty || request.PalletId == Guid.Empty || request.SourceLocationId == Guid.Empty || request.DestinationLocationId == Guid.Empty)
            throw new ArgumentException("Material, pallet, source location and destination location are required.", nameof(request));
        if (request.SourceLocationId == request.DestinationLocationId)
            throw new InvalidOperationException("Source and destination locations must differ.");
        if (!string.IsNullOrWhiteSpace(request.SourceDeviceId)
            && !string.IsNullOrWhiteSpace(request.DestinationDeviceId)
            && !string.Equals(request.SourceDeviceId.Trim(), request.DestinationDeviceId.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Cross-device relocation requires a confirmed multi-device capability.");
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(request.Quantity, 0m);
        ArgumentOutOfRangeException.ThrowIfNegative(request.WeightKg);
        if (request.MaxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(request), "At least one dispatch attempt is required.");
    }

    private static string Require(string? value, string parameterName)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty value is required.", parameterName)
            : value.Trim();
}
