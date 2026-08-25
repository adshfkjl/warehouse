using Warehouse.Wms.Application.Tasks;
using Warehouse.Wms.Domain.Devices;
using Warehouse.Wms.Domain.Outbound;
using Warehouse.Wms.Domain.Tasks;
using WmsTaskScheduler = Warehouse.Wms.Application.Tasks.TaskScheduler;

namespace Warehouse.Wms.Application.Outbound;

public sealed record OutboundLoadingPoint(
    Warehouse.Wms.Domain.MasterData.LoadingPoint LoadingPoint,
    bool IsOccupied,
    bool IsFaulted = false,
    bool IsDisabled = false,
    bool IsLocked = false);

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
    private readonly IResourceLockStore? _resourceLockStore;
    private readonly IReadOnlyList<OutboundLoadingPoint> _loadingPoints;
    private readonly Dictionary<string, OutboundTaskResult> _tasks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskDispatchRequest> _dispatchRequests = new(StringComparer.Ordinal);

    public OutboundTaskService(
        OutboundAllocationService allocations,
        WmsTaskScheduler scheduler,
        IEnumerable<OutboundLoadingPoint> loadingPoints,
        IResourceLockStore? resourceLockStore = null)
    {
        _ = allocations ?? throw new ArgumentNullException(nameof(allocations));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _loadingPoints = loadingPoints?.ToArray() ?? throw new ArgumentNullException(nameof(loadingPoints));
        _resourceLockStore = resourceLockStore;
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

        var loadingPoint = _loadingPoints.FirstOrDefault(item => item.LoadingPoint.Id == request.LoadingPointId)
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
            if (allocation.Order.State == OutboundState.Locked)
                allocation.Order.TransitionTo(OutboundState.Picking);

            var dispatch = await _scheduler.DispatchNextAsync(cancellationToken);
            if (dispatch is not null && ReferenceEquals(dispatch.Request.Task, task))
            {
                var submitted = result with { DispatchResult = dispatch };
                lock (_gate) _tasks[taskNumber] = submitted;
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
        var deviceTaskNumber = result.DispatchResult?.DeviceTaskNumber
            ?? $"sim-{result.Task.TaskNumber}";
        var observation = new DeviceResultObservation(
            deviceTaskNumber,
            1,
            status,
            DeviceObservationSource.Polling,
            DateTimeOffset.UtcNow);
        await _scheduler.ApplyObservationAsync(request, observation);
        return Get(taskNumber);
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

    private static string Require(string? value, string parameterName)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty value is required.", parameterName)
            : value.Trim();
}
