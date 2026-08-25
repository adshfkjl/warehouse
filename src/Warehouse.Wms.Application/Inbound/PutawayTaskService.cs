using Warehouse.Wms.Application.Tasks;
using Warehouse.Wms.Domain.Devices;
using Warehouse.Wms.Domain.Inbound;
using Warehouse.Wms.Domain.Tasks;
using WmsTaskScheduler = Warehouse.Wms.Application.Tasks.TaskScheduler;

namespace Warehouse.Wms.Application.Inbound;

public sealed record PutawayTaskRequest(
    string TaskNumber,
    string DeviceId,
    Guid? LoadingPointId = null,
    string ProtocolVersion = "v1",
    Guid? RequestedLocationId = null,
    decimal LengthMm = 1m,
    decimal WidthMm = 1m,
    decimal HeightMm = 1m,
    string? IdempotencyKey = null,
    int Priority = 0,
    int MaxAttempts = 1);

public sealed record PutawayTaskResult(
    PutawayAllocation Allocation,
    WarehouseTask Task,
    DeviceTask DeviceTask,
    IReadOnlyList<ResourceLock> ResourceLocks,
    TaskDispatchResult? DispatchResult,
    PendingInboundInventory? PendingInventory = null);

/// <summary>
/// Creates the WMS putaway task and submits it through TaskScheduler. This
/// Task 5.2 contract deliberately leaves pending inventory unchanged; Task 5.3
/// is responsible for applying a confirmed physical result.
/// </summary>
public sealed class PutawayTaskService
{
    private readonly object _gate = new();
    private readonly InboundOrderService _inboundOrders;
    private readonly PutawayAllocationService _allocationService;
    private readonly WmsTaskScheduler _scheduler;
    private readonly IResourceLockStore? _resourceLockStore;
    private readonly Dictionary<string, PutawayTaskResult> _byIdempotencyKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PutawayTaskResult> _byTaskNumber = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _requestFingerprints = new(StringComparer.Ordinal);
    private readonly List<ResourceLock> _locks = [];

    public PutawayTaskService(
        InboundOrderService inboundOrders,
        PutawayAllocationService allocationService,
        WmsTaskScheduler scheduler,
        IResourceLockStore? resourceLockStore = null)
    {
        _inboundOrders = inboundOrders ?? throw new ArgumentNullException(nameof(inboundOrders));
        _allocationService = allocationService ?? throw new ArgumentNullException(nameof(allocationService));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _resourceLockStore = resourceLockStore;
    }

    public IReadOnlyCollection<PutawayTaskResult> Tasks
    {
        get { lock (_gate) return _byTaskNumber.Values.ToArray(); }
    }

    public IReadOnlyCollection<ResourceLock> ResourceLocks
    {
        get { lock (_gate) return _locks.ToArray(); }
    }

    public PutawayTaskResult Get(string taskNumber)
    {
        var normalized = Require(taskNumber, nameof(taskNumber));
        lock (_gate)
        {
            return _byTaskNumber.TryGetValue(normalized, out var result)
                ? result
                : throw new KeyNotFoundException($"Putaway task '{normalized}' was not found.");
        }
    }

    public async Task<PutawayTaskResult> CreateAndQueueAsync(
        PendingInboundInventory pendingInventory,
        PutawayTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pendingInventory);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var taskNumber = Require(request.TaskNumber, nameof(request.TaskNumber));
        var deviceId = Require(request.DeviceId, nameof(request.DeviceId));
        var protocolVersion = Require(request.ProtocolVersion, nameof(request.ProtocolVersion));
        var idempotencyKey = Require(
            request.IdempotencyKey ?? $"putaway:{pendingInventory.Id:D}:{taskNumber}",
            nameof(request.IdempotencyKey));
        if (request.MaxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.MaxAttempts, "At least one dispatch attempt is required.");
        }

        lock (_gate)
        {
            if (_byIdempotencyKey.TryGetValue(idempotencyKey, out var existing))
            {
                if (!string.Equals(_requestFingerprints[idempotencyKey], Fingerprint(pendingInventory, request), StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("The putaway idempotency key was reused with a different request.");
                }

                return existing;
            }

            if (_byTaskNumber.ContainsKey(taskNumber))
            {
                throw new InvalidOperationException($"Putaway task '{taskNumber}' already exists.");
            }
        }

        PutawayAllocation? allocation = null;
        ResourceLock[]? locks = null;
        try
        {
            allocation = _allocationService.Allocate(
                pendingInventory,
                new PutawayAllocationRequest(
                    pendingInventory.Id,
                    request.LengthMm,
                    request.WidthMm,
                    request.HeightMm,
                    pendingInventory.WeightKg,
                    request.RequestedLocationId));
            var loadingPoint = _allocationService.SelectLoadingPoint(request.LoadingPointId);
            locks = await AcquireLocksAsync(pendingInventory, allocation, loadingPoint, taskNumber, cancellationToken);
            var task = new WarehouseTask(taskNumber, "Putaway");
            var deviceTask = new DeviceTask(
                idempotencyKey,
                taskNumber,
                deviceId,
                loadingPoint.LoadingPoint.Code,
                allocation.LocationCode,
                loadingPoint.LoadingPoint.Code,
                protocolVersion);
            var dispatchRequest = new TaskDispatchRequest(
                task,
                deviceTask,
                DeviceOperationKind.Inbound,
                request.Priority,
                request.MaxAttempts);
            await _scheduler.EnqueueAsync(dispatchRequest, cancellationToken);
            var result = new PutawayTaskResult(allocation, task, deviceTask, locks, null, pendingInventory);
            lock (_gate)
            {
                _byIdempotencyKey.Add(idempotencyKey, result);
                _byTaskNumber.Add(taskNumber, result);
                _requestFingerprints.Add(idempotencyKey, Fingerprint(pendingInventory, request));
            }

            TryMarkPutawayQueued(pendingInventory);
            return result;
        }
        catch
        {
            if (locks is not null)
            {
                await ReleaseLocksAsync(locks, taskNumber);
            }

            if (allocation is not null)
            {
                _allocationService.Release(pendingInventory.Id);
            }

            throw;
        }
    }

    public async Task<PutawayTaskResult> SubmitAsync(
        PendingInboundInventory pendingInventory,
        PutawayTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        var queued = await CreateAndQueueAsync(pendingInventory, request, cancellationToken);
        if (queued.DispatchResult is not null)
        {
            return queued;
        }

        // A repeated request may find a task that was already dispatched by a
        // previous call. Return its recorded result instead of dispatching a
        // second device command.
        if (queued.Task.State is not TaskState.Created
            and not TaskState.Allocated
            and not TaskState.Queued)
        {
            return queued;
        }

        var dispatch = await _scheduler.DispatchNextAsync(cancellationToken);
        if (dispatch is null || !ReferenceEquals(dispatch.Request.Task, queued.Task))
        {
            throw new InvalidOperationException($"Putaway task '{queued.Task.TaskNumber}' was not dispatched.");
        }

        var submitted = queued with { DispatchResult = dispatch };
        lock (_gate)
        {
            _byIdempotencyKey[queued.DeviceTask.IdempotencyKey] = submitted;
            _byTaskNumber[queued.Task.TaskNumber] = submitted;
        }

        return submitted;
    }

    public Task<PutawayTaskResult> SubmitAsync(
        Guid pendingInboundInventoryId,
        PutawayTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        var pendingInventory = _inboundOrders.PendingInboundInventory
            .FirstOrDefault(item => item.Id == pendingInboundInventoryId)
            ?? throw new KeyNotFoundException($"Pending inbound inventory '{pendingInboundInventoryId}' was not found.");
        return SubmitAsync(pendingInventory, request, cancellationToken);
    }

    public Task<PutawayTaskResult> QueueAsync(
        PendingInboundInventory pendingInventory,
        PutawayTaskRequest request,
        CancellationToken cancellationToken = default)
        => CreateAndQueueAsync(pendingInventory, request, cancellationToken);

    private async Task<ResourceLock[]> AcquireLocksAsync(
        PendingInboundInventory pendingInventory,
        PutawayAllocation allocation,
        PutawayLoadingPointCandidate loadingPoint,
        string taskNumber,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var resources = new List<(string Type, string Id)>
        {
            ("PendingInboundInventory", pendingInventory.Id.ToString("D")),
            ("Location", allocation.LocationId.ToString("D")),
            ("LoadingPoint", loadingPoint.LoadingPoint.Id.ToString("D"))
        };
        if (pendingInventory.PalletId is Guid palletId)
        {
            resources.Add(("Pallet", palletId.ToString("D")));
        }
        else if (!string.IsNullOrWhiteSpace(pendingInventory.PalletCode))
        {
            resources.Add(("Pallet", pendingInventory.PalletCode!));
        }
        else
        {
            throw new InvalidOperationException("A pallet binding is required before creating a putaway task.");
        }

        if (_resourceLockStore is not null)
        {
            var acquired = new List<ResourceLock>(resources.Count);
            try
            {
                foreach (var resource in resources)
                {
                    acquired.Add(await _resourceLockStore.AcquireResourceLockAsync(
                        resource.Type, resource.Id, taskNumber, now, TimeSpan.FromMinutes(15), cancellationToken: cancellationToken));
                }

                return acquired.ToArray();
            }
            catch
            {
                await ReleaseLocksAsync(acquired, taskNumber);
                throw;
            }
        }

        lock (_gate)
        {
            if (resources.Any(resource => _locks.Any(existing =>
                    existing.IsActive(now)
                    && existing.ResourceKey == ResourceLock.BuildResourceKey(resource.Type, resource.Id))))
            {
                throw new InvalidOperationException("One or more putaway resources are already locked by another task.");
            }

            var acquired = resources
                .Select(resource => new ResourceLock(
                    resource.Type,
                    resource.Id,
                    taskNumber,
                    now,
                    TimeSpan.FromMinutes(15)))
                .ToArray();
            _locks.AddRange(acquired);
            return acquired;
        }
    }

    private async Task ReleaseLocksAsync(IReadOnlyList<ResourceLock> locks, string taskNumber)
    {
        var now = DateTimeOffset.UtcNow;
        if (_resourceLockStore is not null)
        {
            foreach (var resourceLock in locks)
            {
                if (resourceLock.IsActive(now))
                {
                    await _resourceLockStore.ReleaseResourceLockAsync(resourceLock.Id, taskNumber, resourceLock.Version, now, resourceLock.LockToken);
                }
            }

            return;
        }

        lock (_gate)
        {
            foreach (var resourceLock in locks.Where(item => item.IsActive(now)))
            {
                resourceLock.Release(taskNumber, resourceLock.Version, now, resourceLock.LockToken);
            }
        }
    }

    private void TryMarkPutawayQueued(PendingInboundInventory pendingInventory)
    {
        try
        {
            var order = _inboundOrders.Get(pendingInventory.OrderNumber);
            if (order.State == InboundState.Received)
            {
                _inboundOrders.MarkPutawayQueued(order.OrderNumber, "system", "上架任务已创建");
            }
        }
        catch (InvalidOperationException)
        {
            // The allocation/task contract remains valid; a partial inbound order
            // or a concurrently changed order is handled by its own workflow.
        }
    }

    private static string Fingerprint(PendingInboundInventory pendingInventory, PutawayTaskRequest request)
        => string.Join(
            "|",
            pendingInventory.Id.ToString("D"),
            request.TaskNumber.Trim(),
            request.DeviceId.Trim(),
            request.LoadingPointId?.ToString("D") ?? "-",
            request.ProtocolVersion.Trim(),
            request.RequestedLocationId?.ToString("D") ?? "-",
            request.LengthMm,
            request.WidthMm,
            request.HeightMm,
            request.Priority,
            request.MaxAttempts);

    private static string Require(string? value, string parameterName)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty value is required.", parameterName)
            : value.Trim();
}
