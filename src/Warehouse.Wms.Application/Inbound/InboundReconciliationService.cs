using Warehouse.Wms.Application.Inventory;
using Warehouse.Wms.Domain.Devices;
using Warehouse.Wms.Domain.Inbound;
using Warehouse.Wms.Domain.Inventory;
using Warehouse.Wms.Domain.Tasks;

namespace Warehouse.Wms.Application.Inbound;

public enum PutawayReconciliationStatus
{
    Completed,
    AlreadyCompleted,
    Exception,
    PhysicalStateUnknown
}

public sealed record PutawayReconciliationResult(
    string TaskNumber,
    PutawayReconciliationStatus Status,
    InventoryTransaction? InventoryTransaction = null);

/// <summary>
/// Applies a confirmed device result in short, idempotent application operations.
/// The device call itself is owned by TaskScheduler and is never held inside an
/// inventory transaction.
/// </summary>
public sealed class InboundReconciliationService
{
    private readonly object _gate = new();
    private readonly InboundOrderService _inboundOrders;
    private readonly PutawayTaskService _putawayTasks;
    private readonly InventoryService _inventory;
    private readonly Dictionary<string, PutawayReconciliationResult> _results = new(StringComparer.Ordinal);
    private readonly HashSet<Guid> _completedPendingInventory = [];

    public InboundReconciliationService(
        InboundOrderService inboundOrders,
        PutawayTaskService putawayTasks,
        InventoryService inventory)
    {
        _inboundOrders = inboundOrders ?? throw new ArgumentNullException(nameof(inboundOrders));
        _putawayTasks = putawayTasks ?? throw new ArgumentNullException(nameof(putawayTasks));
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
    }

    public Task<PutawayReconciliationResult> CompleteAsync(
        string taskNumber,
        CancellationToken cancellationToken = default)
        => ProcessResultAsync(taskNumber, DeviceOperationStatus.Succeeded, cancellationToken);

    public async Task<PutawayReconciliationResult> ProcessResultAsync(
        string taskNumber,
        DeviceOperationStatus status,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedTaskNumber = Require(taskNumber, nameof(taskNumber));
        lock (_gate)
        {
            if (_results.TryGetValue(normalizedTaskNumber, out var existing))
            {
                return existing.Status == PutawayReconciliationStatus.Completed
                    ? existing with { Status = PutawayReconciliationStatus.AlreadyCompleted }
                    : existing;
            }
        }

        var taskResult = _putawayTasks.Get(normalizedTaskNumber);
        var pending = taskResult.PendingInventory
            ?? throw new InvalidOperationException("The putaway task has no pending inbound inventory.");

        if (status is DeviceOperationStatus.PhysicalStateUnknown
            or DeviceOperationStatus.Unknown
            or DeviceOperationStatus.TimedOut)
        {
            MarkUnknown(taskResult);
            var unknown = new PutawayReconciliationResult(
                normalizedTaskNumber,
                PutawayReconciliationStatus.PhysicalStateUnknown);
            lock (_gate) _results[normalizedTaskNumber] = unknown;
            return unknown;
        }

        if (status is DeviceOperationStatus.Failed or DeviceOperationStatus.Offline)
        {
            MarkFailed(taskResult);
            var failure = new PutawayReconciliationResult(
                normalizedTaskNumber,
                PutawayReconciliationStatus.Exception);
            lock (_gate) _results[normalizedTaskNumber] = failure;
            return failure;
        }

        if (status is not DeviceOperationStatus.Succeeded)
        {
            throw new ArgumentException($"Unsupported putaway result '{status}'.", nameof(status));
        }

        lock (_gate)
        {
            if (taskResult.Task.State is TaskState.SentToPlc)
            {
                taskResult.Task.TransitionTo(TaskState.Executing, "reconciliation", "设备成功结果已确认");
            }

            if (taskResult.Task.State is TaskState.Executing)
            {
                taskResult.Task.TransitionTo(TaskState.Succeeded, "reconciliation", "设备上架完成");
            }

            if (taskResult.Task.State is not TaskState.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Putaway task '{normalizedTaskNumber}' cannot be completed from '{taskResult.Task.State}'.");
            }
        }

        var transaction = await _inventory.IncreaseAsync(
            pending.MaterialId,
            pending.PalletId,
            taskResult.Allocation.LocationId,
            pending.BatchNumber,
            pending.Quantity,
            pending.WeightKg,
            new InventoryOperationContext(
                $"putaway:{normalizedTaskNumber}:inventory",
                pending.OrderNumber,
                normalizedTaskNumber,
                "reconciliation",
                "设备确认上架"),
            cancellationToken);

        lock (_gate) _completedPendingInventory.Add(pending.Id);
        ReleaseLocks(taskResult.ResourceLocks, normalizedTaskNumber);
        TryCompleteOrder(pending.OrderNumber);
        var completed = new PutawayReconciliationResult(
            normalizedTaskNumber,
            PutawayReconciliationStatus.Completed,
            transaction);
        lock (_gate) _results[normalizedTaskNumber] = completed;
        return completed;
    }

    private void ReleaseLocks(IReadOnlyList<ResourceLock> locks, string taskNumber)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var resourceLock in locks)
        {
            lock (_gate)
            {
                if (resourceLock.IsActive(now))
                {
                    resourceLock.Release(taskNumber, resourceLock.Version, now, resourceLock.LockToken);
                }
            }
        }
    }

    private void TryCompleteOrder(string orderNumber)
    {
        var order = _inboundOrders.Get(orderNumber);
        var pendingForOrder = _inboundOrders.PendingInboundInventory
            .Where(item => string.Equals(item.OrderNumber, orderNumber, StringComparison.Ordinal))
            .Select(item => item.Id)
            .ToArray();
        lock (_gate)
        {
            if (order.State == InboundState.PutawayQueued
                && pendingForOrder.Length > 0
                && pendingForOrder.All(item => _completedPendingInventory.Contains(item)))
            {
                _inboundOrders.Complete(orderNumber, "reconciliation", "全部上架任务已确认");
            }
        }
    }

    private void MarkFailed(PutawayTaskResult taskResult)
    {
        lock (_gate)
        {
            if (taskResult.Task.State is TaskState.SentToPlc or TaskState.Executing)
            {
                taskResult.Task.TransitionTo(TaskState.Failed, "reconciliation", "设备上架失败");
            }
        }
    }

    private void MarkUnknown(PutawayTaskResult taskResult)
    {
        lock (_gate)
        {
            if (taskResult.Task.State is TaskState.SentToPlc or TaskState.Executing)
            {
                taskResult.Task.TransitionTo(TaskState.TimedOut, "reconciliation", "设备结果无法确认");
                taskResult.Task.TransitionTo(TaskState.PhysicalStateUnknown, "reconciliation", "物理结果未知");
            }
        }
    }

    private static string Require(string value, string parameterName)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty value is required.", parameterName)
            : value.Trim();
}
