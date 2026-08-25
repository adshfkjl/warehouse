using Warehouse.Wms.Application.Inventory;
using Warehouse.Wms.Domain.Devices;
using Warehouse.Wms.Domain.Inventory;
using Warehouse.Wms.Domain.Outbound;
using Warehouse.Wms.Domain.Tasks;
using Warehouse.Wms.Application.Tasks;
using System.Text.Json;

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
    private readonly IBusinessWorkflowStore? _workflowStore;
    private readonly Dictionary<string, int> _workflowVersions = new(StringComparer.Ordinal);

    public OutboundReviewService(OutboundTaskService tasks, InventoryService inventory, IBusinessWorkflowStore? workflowStore = null)
    {
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _workflowStore = workflowStore;
    }

    public async Task<OutboundReviewResult> ProcessDeviceResultAsync(string taskNumber, DeviceOperationStatus status, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_results.TryGetValue(taskNumber, out var existing) && existing.Status != OutboundReviewStatus.ReadyForReview)
                return existing;
        }
        var durable = _workflowStore is null ? null : await _workflowStore.GetAsync("OutboundReview", taskNumber.Trim(), cancellationToken);
        if (durable is not null)
        {
            var restored = JsonSerializer.Deserialize<ReviewState>(durable.SnapshotJson);
            if (restored is not null && restored.Status != OutboundReviewStatus.ReadyForReview)
            {
                var replay = new OutboundReviewResult(taskNumber.Trim(), restored.Status);
                lock (_gate) _results[taskNumber.Trim()] = replay;
                _workflowVersions[taskNumber.Trim()] = durable.Version;
                return replay;
            }
        }
        var current = _tasks.Get(taskNumber);
        var task = status is DeviceOperationStatus.Failed or DeviceOperationStatus.Offline
            && current.Task.State == TaskState.Failed
            ? current
            : await _tasks.ApplyDeviceResultAsync(taskNumber, status, cancellationToken);
        if (status is DeviceOperationStatus.PhysicalStateUnknown or DeviceOperationStatus.Unknown or DeviceOperationStatus.TimedOut)
            return await StoreAsync(taskNumber, new OutboundReviewResult(taskNumber, OutboundReviewStatus.PhysicalStateUnknown), cancellationToken);
        if (status is DeviceOperationStatus.Failed or DeviceOperationStatus.Offline)
        {
            if (task.Allocation.Order.State is OutboundState.Picking)
                task.Allocation.Order.TransitionTo(OutboundState.Exception);
            _tasks.PersistCurrentState(taskNumber);
            return await StoreAsync(taskNumber, new OutboundReviewResult(taskNumber, OutboundReviewStatus.Exception), cancellationToken);
        }
        if (status != DeviceOperationStatus.Succeeded || task.Task.State != TaskState.Succeeded)
            throw new InvalidOperationException("The outbound device result is not ready for review.");
        if (task.Allocation.Order.State == OutboundState.Picking)
            task.Allocation.Order.TransitionTo(OutboundState.AwaitingReview);
        _tasks.PersistCurrentState(taskNumber);
        return await StoreAsync(taskNumber, new OutboundReviewResult(taskNumber, OutboundReviewStatus.ReadyForReview), cancellationToken);
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
        var durable = _workflowStore is null ? null : await _workflowStore.GetAsync("OutboundReview", taskNumber.Trim(), cancellationToken);
        if (durable is not null)
        {
            var restored = JsonSerializer.Deserialize<ReviewState>(durable.SnapshotJson);
            if (restored?.Status == OutboundReviewStatus.Completed)
            {
                var replay = new OutboundReviewResult(taskNumber.Trim(), OutboundReviewStatus.AlreadyCompleted);
                lock (_gate) _results[taskNumber.Trim()] = restored.ToResult(taskNumber.Trim());
                _workflowVersions[taskNumber.Trim()] = durable.Version;
                return replay;
            }
        }
        var task = _tasks.Get(taskNumber);
        if (task.Task.State != TaskState.Succeeded || task.Allocation.Order.State != OutboundState.AwaitingReview)
            throw new InvalidOperationException("The outbound task is not ready for review.");
        if (!request.PhysicalStateConfirmed
            || !string.Equals(request.PalletCode?.Trim(), task.ExpectedPalletCode, StringComparison.OrdinalIgnoreCase)
            || request.WeightKg != task.ExpectedWeightKg)
        {
            task.Allocation.Order.TransitionTo(OutboundState.Exception);
            _tasks.PersistCurrentState(taskNumber);
            return await StoreAsync(taskNumber, new OutboundReviewResult(taskNumber, OutboundReviewStatus.Exception), cancellationToken);
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
        _tasks.PersistCurrentState(taskNumber);
        await _tasks.ReleaseLoadingPointAsync(task);
        await _tasks.ReleaseAllocationResourcesAsync(task);
        return await StoreAsync(taskNumber, new OutboundReviewResult(taskNumber, OutboundReviewStatus.Completed, transaction), cancellationToken);
    }

    private async Task<OutboundReviewResult> StoreAsync(string taskNumber, OutboundReviewResult result, CancellationToken cancellationToken)
    {
        lock (_gate) _results[taskNumber] = result;
        if (_workflowStore is null) return result;
        var key = taskNumber.Trim();
        var version = _workflowVersions.TryGetValue(key, out var current) ? current : 0;
        var snapshot = JsonSerializer.Serialize(new ReviewState(result.Status));
        await _workflowStore.SaveAsync(new BusinessWorkflowSnapshot("OutboundReview", key, version + 1, result.Status.ToString(), snapshot, DateTimeOffset.UtcNow, key), version, "出库复核状态保存", "system", cancellationToken);
        _workflowVersions[key] = version + 1;
        await _workflowStore.RegisterIdempotencyAsync("outbound-review", key, $"review:{key}", "OutboundReview", key, cancellationToken);
        return result;
    }

    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        if (_workflowStore is null) return;
        foreach (var snapshot in await _workflowStore.GetByTypeAsync("OutboundReview", cancellationToken))
        {
            var state = JsonSerializer.Deserialize<ReviewState>(snapshot.SnapshotJson)
                ?? throw new InvalidOperationException($"Outbound review snapshot '{snapshot.AggregateKey}' is invalid.");
            lock (_gate)
            {
                _results[snapshot.AggregateKey] = state.ToResult(snapshot.AggregateKey);
                _workflowVersions[snapshot.AggregateKey] = snapshot.Version;
            }
            if (state.Status == OutboundReviewStatus.ReadyForReview)
            {
                try
                {
                    var task = _tasks.Get(snapshot.AggregateKey);
                    if (task.Allocation.Order.State == OutboundState.Picking)
                        task.Allocation.Order.TransitionTo(OutboundState.AwaitingReview);
                }
                catch (KeyNotFoundException)
                {
                    // Task recovery is independently owned by OutboundTaskService.
                }
            }
        }
    }

    private sealed record ReviewState(OutboundReviewStatus Status)
    {
        public OutboundReviewResult ToResult(string taskNumber) => new(taskNumber, Status);
    }
}
