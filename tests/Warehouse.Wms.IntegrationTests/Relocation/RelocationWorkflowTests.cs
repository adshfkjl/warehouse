using Warehouse.Wms.Application.Devices;
using Warehouse.Wms.Application.Inventory;
using Warehouse.Wms.Application.Relocation;
using Warehouse.Wms.Application.Tasks;
using Warehouse.Wms.Domain.Devices;
using Warehouse.Wms.Domain.Inventory;
using WmsTaskScheduler = Warehouse.Wms.Application.Tasks.TaskScheduler;

namespace Warehouse.Wms.IntegrationTests.Relocation;

public sealed class RelocationWorkflowTests
{
    [Fact]
    public async Task Simulated_transfer_moves_source_to_destination_without_duplicate_ledger()
    {
        var source = Guid.NewGuid();
        var destination = Guid.NewGuid();
        var material = Guid.NewGuid();
        var pallet = Guid.NewGuid();
        var inventory = new InventoryService();
        await inventory.IncreaseAsync(material, pallet, source, "B-01", 4m, 8m, new InventoryOperationContext("integration-seed"));
        var service = new RelocationService(inventory, new WmsTaskScheduler(new ScenarioGateway()));
        var request = new RelocationRequest("integration-relocation-001", material, pallet, source, destination, 4m, 8m, "PLC-01", "B-01");

        var submitted = await service.SubmitAsync(request);
        var completed = await service.CompleteAsync(request.IdempotencyKey);
        var replay = await service.CompleteAsync(request.IdempotencyKey);

        Assert.Equal(RelocationStatus.Executing, submitted.Status);
        Assert.Equal(RelocationStatus.Completed, completed.Status);
        Assert.Equal(RelocationStatus.AlreadyCompleted, replay.Status);
        Assert.Equal(0m, inventory.GetBalance(material, pallet, source, "B-01")!.Quantity);
        Assert.Equal(4m, inventory.GetBalance(material, pallet, destination, "B-01")!.Quantity);
        Assert.Single(inventory.GetTransactions().Where(item => item.Type == InventoryTransactionType.Move));
    }

    private sealed class ScenarioGateway : IWarehouseDeviceGateway
    {
        public Task<DeviceOperationResult> SubmitInboundAsync(DeviceTask task, CancellationToken cancellationToken = default) => Submit(task);
        public Task<DeviceOperationResult> SubmitOutboundAsync(DeviceTask task, CancellationToken cancellationToken = default) => Submit(task);
        public Task<DeviceOperationResult> SubmitTransferAsync(DeviceTask task, CancellationToken cancellationToken = default) => Submit(task);
        public Task<DeviceResultObservation?> GetStatusAsync(string deviceTaskNumber, CancellationToken cancellationToken = default) => Task.FromResult<DeviceResultObservation?>(null);
        public Task<DeviceOperationResult> TestConnectionAsync(string deviceId, CancellationToken cancellationToken = default) => Task.FromResult(new DeviceOperationResult($"connection:{deviceId}", DeviceOperationStatus.Succeeded));
        public Task<DeviceOperationResult> RequestStopAsync(DeviceTask task, CancellationToken cancellationToken = default) => Task.FromResult(new DeviceOperationResult(task.IdempotencyKey, DeviceOperationStatus.StopConfirmed));
        private static Task<DeviceOperationResult> Submit(DeviceTask task) => Task.FromResult(new DeviceOperationResult(task.IdempotencyKey, DeviceOperationStatus.Accepted, $"sim-{task.WmsTaskId}"));
    }
}
