using System.Text.RegularExpressions;
using Warehouse.Wms.Domain.Inventory;
using Warehouse.Wms.Domain.Stocktaking;
using Warehouse.Wms.Application.Devices;
using Warehouse.Wms.Application.Tasks;
using Warehouse.Wms.Domain.Devices;
using Warehouse.Wms.Domain.Tasks;
using WmsTaskScheduler = Warehouse.Wms.Application.Tasks.TaskScheduler;
using System.Text.Json;

namespace Warehouse.Wms.Application.Stocktaking;

public sealed record StocktakingInventoryItem(
    string LocationCode,
    string ZoneCode,
    string MaterialCode,
    string? BatchNumber,
    string PalletCode,
    Guid MaterialId,
    Guid PalletId,
    decimal Quantity,
    decimal WeightKg,
    InventoryStatus Status);

public sealed record StocktakingRequest(
    string TaskNumber,
    string? ZoneCode = null,
    string? MaterialCode = null,
    string? BatchNumber = null,
    string? PalletCode = null,
    string? LocationRangeStart = null,
    string? LocationRangeEnd = null);

public sealed record StocktakingDeviceTaskResult(
    Guid ItemId,
    WarehouseTask Task,
    DeviceTask DeviceTask,
    string LoadingPointCode,
    ResourceLock? LoadingPointLock = null);

public sealed class StocktakingService
{
    private static readonly Regex ShelfRangePattern = new("^(?<prefix>[A-Za-z]+)(?<number>\\d+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LocationPattern = new("^(?<prefix>[A-Za-z]+)(?<shelf>\\d+)-(?<slot>\\d+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly object _gate = new();
    private readonly IReadOnlyList<StocktakingInventoryItem> _inventory;
    private readonly WmsTaskScheduler? _scheduler;
    private readonly IResourceLockStore? _resourceLockStore;
    private readonly IBusinessWorkflowStore? _workflowStore;
    private readonly Dictionary<string, StocktakingTask> _tasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, StocktakingDeviceTaskResult> _deviceTasks = [];
    private readonly HashSet<string> _activeLoadingPoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, ResourceLock> _loadingPointLocks = [];
    private readonly Dictionary<string, int> _workflowVersions = new(StringComparer.OrdinalIgnoreCase);
    private bool _restoring;

    public StocktakingService(IEnumerable<StocktakingInventoryItem> inventory, WmsTaskScheduler? scheduler = null, IResourceLockStore? resourceLockStore = null, IBusinessWorkflowStore? workflowStore = null)
    {
        _inventory = inventory?.ToArray() ?? throw new ArgumentNullException(nameof(inventory));
        _scheduler = scheduler;
        _resourceLockStore = resourceLockStore;
        _workflowStore = workflowStore;
    }

    public IReadOnlyCollection<StocktakingTask> Tasks
    {
        get { lock (_gate) return _tasks.Values.ToArray(); }
    }

    public StocktakingTask Create(StocktakingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var taskNumber = Require(request.TaskNumber, nameof(request.TaskNumber));
        lock (_gate)
        {
            if (_tasks.ContainsKey(taskNumber)) throw new InvalidOperationException($"Stocktaking task '{taskNumber}' already exists.");
        }

        var selected = _inventory
            .Where(item => item.Status is InventoryStatus.Available or InventoryStatus.Locked)
            .Where(item => Match(item, request))
            .OrderBy(item => item.LocationCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.MaterialCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.PalletCode, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (selected.Length == 0) throw new InvalidOperationException("The stocktaking range contains no inventory.");
        var task = new StocktakingTask(taskNumber, selected.Select(item => new StocktakingItem(
            item.LocationCode, item.MaterialId, item.PalletId, item.Quantity, item.WeightKg)));
        lock (_gate) _tasks.Add(taskNumber, task);
        Persist(task);
        return task;
    }

    public StocktakingTask Start(string taskNumber)
    {
        var task = Get(taskNumber);
        task.TransitionTo(StocktakingState.Pending);
        task.TransitionTo(StocktakingState.Running);
        Persist(task);
        return task;
    }

    public StocktakingTask RecordCount(string taskNumber, Guid itemId, decimal quantity, decimal weightKg, string? loadingPointCode = null)
    {
        var task = Get(taskNumber);
        if (task.State != StocktakingState.Running)
            throw new InvalidOperationException("Stocktaking task is not running.");
        var item = task.Items.FirstOrDefault(candidate => candidate.Id == itemId)
            ?? throw new KeyNotFoundException($"Stocktaking item '{itemId}' was not found.");
        item.RecordCount(quantity, weightKg, loadingPointCode);
        Persist(task);
        return task;
    }

    public async Task<StocktakingDeviceTaskResult> QueueDeviceTaskAsync(
        string taskNumber,
        Guid itemId,
        string deviceId,
        string loadingPointCode,
        CancellationToken cancellationToken = default)
    {
        var scheduler = _scheduler ?? throw new InvalidOperationException("A task scheduler is required for device-assisted stocktaking.");
        var task = Get(taskNumber);
        if (task.State != StocktakingState.Running) throw new InvalidOperationException("Stocktaking task is not running.");
        var item = task.Items.FirstOrDefault(candidate => candidate.Id == itemId)
            ?? throw new KeyNotFoundException($"Stocktaking item '{itemId}' was not found.");
        var loadingPoint = Require(loadingPointCode, nameof(loadingPointCode));
        ResourceLock? persistentLock = null;
        lock (_gate)
        {
            if (_deviceTasks.TryGetValue(itemId, out var existing)) return existing;
            if (_resourceLockStore is null && !_activeLoadingPoints.Add(loadingPoint))
                throw new InvalidOperationException($"Loading point '{loadingPoint}' is occupied by another stocktaking item.");
        }
        try
        {
            item.BeginCounting();
            var warehouseTask = new WarehouseTask($"{task.TaskNumber}-ITEM-{itemId:N}", "StocktakingOutbound");
            if (_resourceLockStore is not null)
            {
                persistentLock = await _resourceLockStore.AcquireResourceLockAsync(
                    "LoadingPoint", loadingPoint, warehouseTask.TaskNumber, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(15), cancellationToken: cancellationToken);
            }
            var deviceTask = new DeviceTask(
                $"stocktaking:{task.TaskNumber}:{itemId:D}", warehouseTask.TaskNumber, Require(deviceId, nameof(deviceId)),
                item.LocationCode, null, loadingPoint, "v1");
            var dispatch = new TaskDispatchRequest(warehouseTask, deviceTask, DeviceOperationKind.Outbound, 0, 1);
            await scheduler.EnqueueAsync(dispatch, cancellationToken);
            var result = new StocktakingDeviceTaskResult(itemId, warehouseTask, deviceTask, loadingPoint);
            lock (_gate)
            {
                _deviceTasks.Add(itemId, result with { LoadingPointLock = persistentLock });
                if (persistentLock is not null) _loadingPointLocks[itemId] = persistentLock;
            }
            Persist(task);
            return result;
        }
        catch
        {
            lock (_gate) _activeLoadingPoints.Remove(loadingPoint);
            if (persistentLock is not null)
            {
                await _resourceLockStore!.ReleaseResourceLockAsync(
                    persistentLock.Id, persistentLock.OwnerTaskNumber, persistentLock.Version,
                    DateTimeOffset.UtcNow, persistentLock.LockToken, CancellationToken.None);
            }
            throw;
        }
    }

    public StocktakingTask Complete(string taskNumber)
    {
        var task = Get(taskNumber);
        if (task.State != StocktakingState.Running)
            throw new InvalidOperationException("Stocktaking task is not running.");
        if (task.Items.Any(item => item.State == StocktakingItemState.Pending || item.State == StocktakingItemState.Counting))
            throw new InvalidOperationException("Every stocktaking item must be counted before completion.");
        task.TransitionTo(task.Items.Any(item => item.State == StocktakingItemState.Difference)
            ? StocktakingState.CompletedWithErrors
            : StocktakingState.Completed);
        ReleaseAllLoadingPointLocksAsync().GetAwaiter().GetResult();
        Persist(task);
        return task;
    }

    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        if (_workflowStore is null) return;
        var snapshots = await _workflowStore.GetByTypeAsync("Stocktaking", cancellationToken);
        var activeLocks = _resourceLockStore is null
            ? Array.Empty<ResourceLock>()
            : (await _resourceLockStore.GetActiveResourceLocksAsync(cancellationToken: cancellationToken)).ToArray();
        lock (_gate) _restoring = true;
        try
        {
            foreach (var snapshot in snapshots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                StocktakingWorkflowState? state;
                try { state = JsonSerializer.Deserialize<StocktakingWorkflowState>(snapshot.SnapshotJson); }
                catch (JsonException) { continue; }
                if (state is null || string.IsNullOrWhiteSpace(state.TaskNumber)) continue;
                lock (_gate) if (_tasks.ContainsKey(state.TaskNumber)) continue;
                try
                {
                    var items = state.Items.Select(itemState =>
                    {
                        var item = new StocktakingItem(itemState.LocationCode, itemState.MaterialId, itemState.PalletId, itemState.BookQuantity, itemState.BookWeightKg, itemState.Id);
                        item.RestoreCount(itemState.ActualQuantity, itemState.ActualWeightKg, itemState.LoadingPointCode, itemState.State);
                        return item;
                    }).ToArray();
                    var task = new StocktakingTask(state.TaskNumber, items, state.TaskId);
                    task.RestoreState(state.State);
                    lock (_gate) _tasks[state.TaskNumber] = task;
                    foreach (var deviceState in state.DeviceTasks)
                    {
                        var warehouseTask = new WarehouseTask(deviceState.TaskNumber, deviceState.TaskType, deviceState.CreatedAt, deviceState.TaskId);
                        RestoreTaskState(warehouseTask, deviceState.TaskState);
                        var deviceTask = new DeviceTask(deviceState.IdempotencyKey, deviceState.TaskNumber, deviceState.DeviceId, deviceState.SourceLocation, deviceState.DestinationLocation, deviceState.LoadingPoint, deviceState.ProtocolVersion);
                        var dispatch = new TaskDispatchRequest(warehouseTask, deviceTask, DeviceOperationKind.Outbound) { DeviceTaskNumber = deviceState.DeviceTaskNumber };
                        var locks = activeLocks.Where(x => deviceState.ResourceLockIds.Contains(x.Id) || x.OwnerTaskNumber == deviceState.TaskNumber).ToArray();
                        lock (_gate)
                        {
                            _deviceTasks[deviceState.ItemId] = new StocktakingDeviceTaskResult(deviceState.ItemId, warehouseTask, deviceTask, deviceState.LoadingPointCode, locks.FirstOrDefault());
                            if (locks.FirstOrDefault() is { } loadingLock) _loadingPointLocks[deviceState.ItemId] = loadingLock;
                        }
                    }
                    lock (_gate) _workflowVersions[state.TaskNumber] = snapshot.Version;
                }
                catch (InvalidOperationException) { /* Worker marks malformed state as blocked. */ }
            }
        }
        finally { lock (_gate) _restoring = false; }
    }

    private void Persist(StocktakingTask task)
    {
        if (_workflowStore is null || _restoring) return;
        var key = task.TaskNumber;
        var version = _workflowVersions.TryGetValue(key, out var current) ? current : 0;
        StocktakingDeviceTaskResult[] deviceTasks;
        lock (_gate) deviceTasks = task.Items.Select(item => _deviceTasks.TryGetValue(item.Id, out var result) ? result : null).Where(x => x is not null).Cast<StocktakingDeviceTaskResult>().ToArray();
        var state = new StocktakingWorkflowState(
            task.Id, task.TaskNumber, task.State,
            task.Items.Select(item => new StocktakingItemStateSnapshot(item.Id, item.LocationCode, item.MaterialId, item.PalletId, item.BookQuantity, item.BookWeightKg, item.ActualQuantity, item.ActualWeightKg, item.LoadingPointCode, item.State)).ToArray(),
            deviceTasks.Select(result => new StocktakingDeviceTaskState(result.ItemId, result.Task.Id, result.Task.TaskNumber, result.Task.TaskType, result.Task.State, result.Task.CreatedAt, result.DeviceTask.IdempotencyKey, result.DeviceTask.DeviceId, result.DeviceTask.ProtocolVersion, result.DeviceTask.SourceLocation, result.DeviceTask.DestinationLocation, result.DeviceTask.LoadingPoint, result.LoadingPointCode, result.LoadingPointLock is null ? Array.Empty<Guid>() : new[] { result.LoadingPointLock.Id }, result.DeviceTask.IdempotencyKey)).ToArray());
        var json = JsonSerializer.Serialize(state);
        _workflowStore.SaveAsync(new BusinessWorkflowSnapshot("Stocktaking", key, version + 1, task.State.ToString(), json, DateTimeOffset.UtcNow, key), version, "盘点业务状态保存", "system").GetAwaiter().GetResult();
        _workflowVersions[key] = version + 1;
        _workflowStore.RegisterIdempotencyAsync("stocktaking", key, $"stocktaking:{key}", "Stocktaking", key).GetAwaiter().GetResult();
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
            _ => throw new InvalidOperationException($"Stocktaking task state '{target}' cannot be restored.")
        };
        foreach (var state in path) task.TransitionTo(state, "recovery", "从盘点业务快照恢复");
    }

    private sealed record StocktakingWorkflowState(Guid TaskId, string TaskNumber, StocktakingState State, IReadOnlyList<StocktakingItemStateSnapshot> Items, IReadOnlyList<StocktakingDeviceTaskState> DeviceTasks);
    private sealed record StocktakingItemStateSnapshot(Guid Id, string LocationCode, Guid MaterialId, Guid PalletId, decimal BookQuantity, decimal BookWeightKg, decimal? ActualQuantity, decimal? ActualWeightKg, string? LoadingPointCode, StocktakingItemState State);
    private sealed record StocktakingDeviceTaskState(Guid ItemId, Guid TaskId, string TaskNumber, string TaskType, TaskState TaskState, DateTimeOffset CreatedAt, string IdempotencyKey, string DeviceId, string ProtocolVersion, string? SourceLocation, string? DestinationLocation, string? LoadingPoint, string LoadingPointCode, IReadOnlyList<Guid> ResourceLockIds, string DeviceTaskNumber);

    private async Task ReleaseAllLoadingPointLocksAsync()
    {
        ResourceLock[] locks;
        lock (_gate) locks = _loadingPointLocks.Values.Where(item => item.IsActive()).ToArray();
        if (_resourceLockStore is not null)
        {
            foreach (var resourceLock in locks)
                await _resourceLockStore.ReleaseResourceLockAsync(resourceLock.Id, resourceLock.OwnerTaskNumber, resourceLock.Version, DateTimeOffset.UtcNow, resourceLock.LockToken);
        }
        else
        {
            foreach (var resourceLock in locks) resourceLock.Release(resourceLock.OwnerTaskNumber, resourceLock.Version, DateTimeOffset.UtcNow, resourceLock.LockToken);
        }
    }

    public StocktakingTask Get(string taskNumber)
    {
        var normalized = Require(taskNumber, nameof(taskNumber));
        lock (_gate)
        {
            return _tasks.TryGetValue(normalized, out var task)
                ? task
                : throw new KeyNotFoundException($"Stocktaking task '{normalized}' was not found.");
        }
    }

    private static bool Match(StocktakingInventoryItem item, StocktakingRequest request)
        => (request.ZoneCode is null || string.Equals(item.ZoneCode, request.ZoneCode.Trim(), StringComparison.OrdinalIgnoreCase))
           && (request.MaterialCode is null || string.Equals(item.MaterialCode, request.MaterialCode.Trim(), StringComparison.OrdinalIgnoreCase))
           && (request.BatchNumber is null || string.Equals(item.BatchNumber, request.BatchNumber.Trim(), StringComparison.OrdinalIgnoreCase))
           && (request.PalletCode is null || string.Equals(item.PalletCode, request.PalletCode.Trim(), StringComparison.OrdinalIgnoreCase))
           && InLocationRange(item.LocationCode, request.LocationRangeStart, request.LocationRangeEnd);

    private static bool InLocationRange(string locationCode, string? start, string? end)
    {
        if (string.IsNullOrWhiteSpace(start) && string.IsNullOrWhiteSpace(end)) return true;
        if (string.IsNullOrWhiteSpace(start) || string.IsNullOrWhiteSpace(end)) throw new ArgumentException("Both location range endpoints are required.");
        var normalizedStart = start.Trim();
        var normalizedEnd = end.Trim();
        var shelfStart = ShelfRangePattern.Match(normalizedStart);
        var shelfEnd = ShelfRangePattern.Match(normalizedEnd);
        if (shelfStart.Success && shelfEnd.Success && string.Equals(shelfStart.Groups["prefix"].Value, shelfEnd.Groups["prefix"].Value, StringComparison.OrdinalIgnoreCase))
        {
            var parsedLocation = ParseLocation(locationCode);
            if (parsedLocation is null) return false;
            var min = int.Parse(shelfStart.Groups["number"].Value, System.Globalization.CultureInfo.InvariantCulture);
            var max = int.Parse(shelfEnd.Groups["number"].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (min > max) (min, max) = (max, min);
            return string.Equals(parsedLocation.Value.Prefix, shelfStart.Groups["prefix"].Value, StringComparison.OrdinalIgnoreCase)
                && parsedLocation.Value.Shelf >= min && parsedLocation.Value.Shelf <= max;
        }

        var parsedLocation2 = ParseLocation(locationCode);
        var parsedStart = ParseLocation(normalizedStart);
        var parsedEnd = ParseLocation(normalizedEnd);
        if (parsedLocation2 is null || parsedStart is null || parsedEnd is null) return false;
        var lower = Compare(parsedStart.Value, parsedEnd.Value) <= 0 ? parsedStart.Value : parsedEnd.Value;
        var upper = Compare(parsedStart.Value, parsedEnd.Value) <= 0 ? parsedEnd.Value : parsedStart.Value;
        return Compare(parsedLocation2.Value, lower) >= 0 && Compare(parsedLocation2.Value, upper) <= 0;
    }

    private static (string Prefix, int Number)? ParseShelf(string value)
    {
        var match = ShelfRangePattern.Match(value);
        return match.Success
            ? (match.Groups["prefix"].Value, int.Parse(match.Groups["number"].Value, System.Globalization.CultureInfo.InvariantCulture))
            : null;
    }

    private static (string Prefix, int Shelf, int Slot)? ParseLocation(string value)
    {
        var match = LocationPattern.Match(value);
        return match.Success
            ? (match.Groups["prefix"].Value, int.Parse(match.Groups["shelf"].Value, System.Globalization.CultureInfo.InvariantCulture), int.Parse(match.Groups["slot"].Value, System.Globalization.CultureInfo.InvariantCulture))
            : null;
    }

    private static int Compare((string Prefix, int Shelf, int Slot) left, (string Prefix, int Shelf, int Slot) right)
    {
        var prefix = string.Compare(left.Prefix, right.Prefix, StringComparison.OrdinalIgnoreCase);
        return prefix != 0 ? prefix : left.Shelf != right.Shelf ? left.Shelf.CompareTo(right.Shelf) : left.Slot.CompareTo(right.Slot);
    }

    private static string Require(string? value, string parameterName)
        => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A non-empty value is required.", parameterName) : value.Trim();
}
