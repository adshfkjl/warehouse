using Warehouse.Wms.Application.Devices;
using Warehouse.Wms.Application.Inbound;
using Warehouse.Wms.Application.Tasks;
using Warehouse.Wms.Domain.Devices;
using Warehouse.Wms.Domain.MasterData;
using Warehouse.Wms.Domain.Tasks;
using WmsTaskScheduler = Warehouse.Wms.Application.Tasks.TaskScheduler;

namespace Warehouse.Wms.IntegrationTests.Inbound;

public sealed class PutawayRecoveryTests
{
    [Fact]
    public async Task Putaway_task_restores_stable_pending_id_and_device_context_after_restart()
    {
        var workflows = new InMemoryBusinessWorkflowStore();
        var material = Guid.NewGuid();
        var inbound = new InboundOrderService(workflows);
        var order = inbound.Create("IN-PUTAWAY-RECOVERY", [new InboundLineRequest(material, 1m)]);
        var receipt = inbound.Receive(order.OrderNumber, order.Lines.Single().Id, new InboundReceiptRequest("receipt-putaway-recovery", 1m, PalletCode: "PLT-RECOVERY"));
        var location = new Location(Guid.NewGuid(), "PUT-RECOVERY", 1, 1000m, 1000m, 1000m, 1000m);
        var loadingPoint = new LoadingPoint("LP-RECOVERY", "上架口");
        var allocation = new PutawayAllocationService(
            [new PutawayLocationCandidate(location)],
            [new PutawayLoadingPointCandidate(loadingPoint, true)]);
        var gateway = new RecoveryGateway();
        var scheduler = new WmsTaskScheduler(gateway);
        var first = new PutawayTaskService(inbound, allocation, scheduler, workflowStore: workflows);
        var queued = await first.SubmitAsync(receipt.PendingInbound, new PutawayTaskRequest("PUT-RECOVERY-TASK", "PLC-RECOVERY", loadingPoint.Id));

        var inboundRestart = new InboundOrderService(workflows.CreateRestartedInstance());
        await inboundRestart.RestoreAsync();
        var allocationRestart = new PutawayAllocationService([new PutawayLocationCandidate(location)], [new PutawayLoadingPointCandidate(loadingPoint, true)]);
        var restartedScheduler = new WmsTaskScheduler(gateway);
        var restarted = new PutawayTaskService(inboundRestart, allocationRestart, restartedScheduler, workflowStore: workflows.CreateRestartedInstance());
        await restarted.RestoreAsync();

        var restored = restarted.Get("PUT-RECOVERY-TASK");
        Assert.Equal(receipt.PendingInbound.Id, restored.PendingInventory!.Id);
        Assert.Equal(queued.DeviceTask.IdempotencyKey, restored.DeviceTask.IdempotencyKey);
        Assert.Equal(queued.Allocation.LocationId, restored.Allocation.LocationId);
        Assert.Equal(queued.Task.State, restored.Task.State);
    }

    private sealed class RecoveryGateway : IWarehouseDeviceGateway
    {
        public Task<DeviceOperationResult> SubmitInboundAsync(DeviceTask task, CancellationToken cancellationToken = default) => Task.FromResult(new DeviceOperationResult(task.IdempotencyKey, DeviceOperationStatus.Accepted, $"device-{task.WmsTaskId}"));
        public Task<DeviceOperationResult> SubmitOutboundAsync(DeviceTask task, CancellationToken cancellationToken = default) => SubmitInboundAsync(task, cancellationToken);
        public Task<DeviceOperationResult> SubmitTransferAsync(DeviceTask task, CancellationToken cancellationToken = default) => SubmitInboundAsync(task, cancellationToken);
        public Task<DeviceResultObservation?> GetStatusAsync(string deviceTaskNumber, CancellationToken cancellationToken = default) => Task.FromResult<DeviceResultObservation?>(null);
        public Task<DeviceOperationResult> TestConnectionAsync(string deviceId, CancellationToken cancellationToken = default) => Task.FromResult(new DeviceOperationResult(deviceId, DeviceOperationStatus.Succeeded));
        public Task<DeviceOperationResult> RequestStopAsync(DeviceTask task, CancellationToken cancellationToken = default) => Task.FromResult(new DeviceOperationResult(task.IdempotencyKey, DeviceOperationStatus.StopConfirmed));
    }
}
