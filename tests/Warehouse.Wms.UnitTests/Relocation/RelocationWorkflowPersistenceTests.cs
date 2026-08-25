using Warehouse.Wms.Application.Devices;
using Warehouse.Wms.Application.Inventory;
using Warehouse.Wms.Application.Relocation;
using Warehouse.Wms.Application.Tasks;
using Warehouse.Wms.Domain.Devices;
using WmsTaskScheduler = Warehouse.Wms.Application.Tasks.TaskScheduler;
using Warehouse.Wms.Domain.Inventory;

namespace Warehouse.Wms.UnitTests.Relocation;

public sealed class RelocationWorkflowPersistenceTests
{
    [Fact]
    public async Task Restore_rehydrates_relocation_and_replays_submit_without_duplicate()
    {
        var store = new InMemoryBusinessWorkflowStore();
        var inventory = new InventoryService();
        var material = Guid.NewGuid();
        var pallet = Guid.NewGuid();
        var source = Guid.NewGuid();
        var destination = Guid.NewGuid();
        await inventory.IncreaseAsync(material, pallet, source, "B-01", 1m, 1m, new InventoryOperationContext("seed"));
        var first = new RelocationService(inventory, new WmsTaskScheduler(new Gateway()), workflowStore: store);
        var created = await first.SubmitAsync(new RelocationRequest("rel-persist-1", material, pallet, source, destination, 1m, 1m, "PLC-01", "B-01"));

        var restarted = new RelocationService(inventory, new WmsTaskScheduler(new Gateway()), workflowStore: store.CreateRestartedInstance());
        await restarted.RestoreAsync();
        var restored = restarted.Get("rel-persist-1");
        var replay = await restarted.SubmitAsync(new RelocationRequest("rel-persist-1", material, pallet, source, destination, 1m, 1m, "PLC-01", "B-01"));

        Assert.Equal(created.Order.Id, restored.Order.Id);
        Assert.Equal(created.Task.TaskNumber, restored.Task.TaskNumber);
        Assert.Equal(created.Status, restored.Status);
        Assert.Same(restored.Order, replay.Order);
    }

    private sealed class Gateway : IWarehouseDeviceGateway
    {
        public Task<DeviceOperationResult> SubmitInboundAsync(DeviceTask task, CancellationToken cancellationToken = default) => Accepted(task);
        public Task<DeviceOperationResult> SubmitOutboundAsync(DeviceTask task, CancellationToken cancellationToken = default) => Accepted(task);
        public Task<DeviceOperationResult> SubmitTransferAsync(DeviceTask task, CancellationToken cancellationToken = default) => Accepted(task);
        public Task<DeviceResultObservation?> GetStatusAsync(string deviceTaskNumber, CancellationToken cancellationToken = default) => Task.FromResult<DeviceResultObservation?>(null);
        public Task<DeviceOperationResult> TestConnectionAsync(string deviceId, CancellationToken cancellationToken = default) => Task.FromResult(new DeviceOperationResult($"connection:{deviceId}", DeviceOperationStatus.Succeeded));
        public Task<DeviceOperationResult> RequestStopAsync(DeviceTask task, CancellationToken cancellationToken = default) => Accepted(task);
        private static Task<DeviceOperationResult> Accepted(DeviceTask task) => Task.FromResult(new DeviceOperationResult(task.IdempotencyKey, DeviceOperationStatus.Accepted, $"dev-{task.WmsTaskId}"));
    }
}
