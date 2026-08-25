using Warehouse.Wms.Application.Inventory;
using Warehouse.Wms.Domain.Devices;
using Warehouse.Wms.Domain.Inventory;
using Warehouse.Wms.Domain.Outbound;
using Warehouse.Wms.Domain.Tasks;

namespace Warehouse.Wms.Application.Outbound;

public sealed record OutboundReviewRequest(string PalletCode, decimal WeightKg, bool PhysicalStateConfirmed);
public enum OutboundReviewStatus { ReadyForReview, Completed, AlreadyCompleted, Exception, PhysicalStateUnknown }
public sealed record OutboundReviewResult(string TaskNumber, OutboundReviewStatus Status, InventoryTransaction? InventoryTransaction = null);

public sealed class OutboundReviewService
{
    private readonly object _gate = new();
    private readonly OutboundTaskService _tasks;
    private readonly InventoryService _inventory;
    private readonly Dictionary<string, OutboundReviewResult> _results = new(StringComparer.Ordinal);

    public OutboundReviewService(OutboundTaskService tasks, InventoryService inventory)
    {
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
    }

    public async Task<OutboundReviewResult> ProcessDeviceResultAsync(string taskNumber, DeviceOperationStatus status, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_results.TryGetValue(taskNumber, out var existing) && existing.Status != OutboundReviewStatus.ReadyForReview)
                return existing;
        }
        var current = _tasks.Get(taskNumber);
        var task = status is DeviceOperationStatus.Failed or DeviceOperationStatus.Offline
            && current.Task.State == TaskState.Failed
            ? current
            : await _tasks.ApplyDeviceResultAsync(taskNumber, status, cancellationToken);
        if (status is DeviceOperationStatus.PhysicalStateUnknown or DeviceOperationStatus.Unknown or DeviceOperationStatus.TimedOut)
            return Store(taskNumber, new OutboundReviewResult(taskNumber, OutboundReviewStatus.PhysicalStateUnknown));
        if (status is DeviceOperationStatus.Failed or DeviceOperationStatus.Offline)
        {
            if (task.Allocation.Order.State is OutboundState.Picking)
                task.Allocation.Order.TransitionTo(OutboundState.Exception);
            return Store(taskNumber, new OutboundReviewResult(taskNumber, OutboundReviewStatus.Exception));
        }
        if (status != DeviceOperationStatus.Succeeded || task.Task.State != TaskState.Succeeded)
            throw new InvalidOperationException("The outbound device result is not ready for review.");
        if (task.Allocation.Order.State == OutboundState.Picking)
            task.Allocation.Order.TransitionTo(OutboundState.AwaitingReview);
        return Store(taskNumber, new OutboundReviewResult(taskNumber, OutboundReviewStatus.ReadyForReview));
    }

    public async Task<OutboundReviewResult> ReviewAsync(string taskNumber, OutboundReviewRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_results.TryGetValue(taskNumber, out var existing) && existing.Status == OutboundReviewStatus.Completed)
                return existing with { Status = OutboundReviewStatus.AlreadyCompleted };
        }
        var task = _tasks.Get(taskNumber);
        if (task.Task.State != TaskState.Succeeded || task.Allocation.Order.State != OutboundState.AwaitingReview)
            throw new InvalidOperationException("The outbound task is not ready for review.");
        if (!request.PhysicalStateConfirmed
            || !string.Equals(request.PalletCode?.Trim(), task.ExpectedPalletCode, StringComparison.OrdinalIgnoreCase)
            || request.WeightKg != task.ExpectedWeightKg)
        {
            task.Allocation.Order.TransitionTo(OutboundState.Exception);
            return Store(taskNumber, new OutboundReviewResult(taskNumber, OutboundReviewStatus.Exception));
        }

        var transaction = await _inventory.DecreaseAsync(
            task.Allocation.Source.MaterialId,
            task.Allocation.Source.PalletId,
            task.Allocation.Source.LocationId,
            task.Allocation.Source.BatchNumber,
            task.Allocation.Quantity,
            task.ExpectedWeightKg,
            new InventoryOperationContext($"outbound:{taskNumber}:decrease", task.Allocation.Order.OrderNumber, taskNumber, "review", "出库复核通过"),
            cancellationToken);
        task.Allocation.Order.TransitionTo(OutboundState.Completed);
        await _tasks.ReleaseLoadingPointAsync(task);
        await _tasks.ReleaseAllocationResourcesAsync(task);
        return Store(taskNumber, new OutboundReviewResult(taskNumber, OutboundReviewStatus.Completed, transaction));
    }

    private OutboundReviewResult Store(string taskNumber, OutboundReviewResult result)
    {
        lock (_gate) _results[taskNumber] = result;
        return result;
    }
}
