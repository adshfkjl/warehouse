using Warehouse.Wms.Application.Inventory;
using Warehouse.Wms.Application.Devices;
using Warehouse.Wms.Application.Relocation;
using Warehouse.Wms.Application.Tasks;
using Warehouse.Wms.Domain.Devices;
using Warehouse.Wms.Domain.Inventory;
using Warehouse.Wms.Domain.MasterData;
using Warehouse.Wms.Domain.Relocation;
using WmsTaskScheduler = Warehouse.Wms.Application.Tasks.TaskScheduler;

namespace Warehouse.Wms.UnitTests.Relocation;

public sealed class RelocationServiceTests
{
    [Fact]
    public async Task Rejects_missing_source_and_occupied_destination()
    {
        var inventory = new InventoryService();
        var service = CreateService(inventory);
        var request = new RelocationRequest("relocate-001", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1m, 1m, "PLC-01");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SubmitAsync(request));

        var source = Guid.NewGuid();
        var destination = Guid.NewGuid();
        var material = Guid.NewGuid();
        var pallet = Guid.NewGuid();
        await inventory.IncreaseAsync(material, pallet, destination, null, 1m, 1m, new InventoryOperationContext("seed-destination"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SubmitAsync(
            request with { IdempotencyKey = "relocate-002", SourceLocationId = source, DestinationLocationId = destination, MaterialId = material, PalletId = pallet }));
    }

    [Fact]
    public async Task Successful_device_result_moves_inventory_once_and_releases_locks()
    {
        var source = Guid.NewGuid();
        var destination = Guid.NewGuid();
        var material = Guid.NewGuid();
        var pallet = Guid.NewGuid();
        var inventory = new InventoryService();
        await inventory.IncreaseAsync(material, pallet, source, "B-01", 3m, 6m, new InventoryOperationContext("seed-source"));
        var service = CreateService(inventory, DeviceOperationStatus.Accepted);
        var request = new RelocationRequest("relocate-003", material, pallet, source, destination, 2m, 4m, "PLC-01");

        var first = await service.SubmitAsync(request);
        var completed = await service.CompleteAsync(request.IdempotencyKey);
        var replay = await service.CompleteAsync(request.IdempotencyKey);

        Assert.Equal(RelocationStatus.Completed, completed.Status);
        Assert.Equal(RelocationStatus.AlreadyCompleted, replay.Status);
        Assert.Equal(1m, inventory.GetBalance(material, pallet, source, "B-01")!.Quantity);
        Assert.Equal(2m, inventory.GetBalance(material, pallet, destination, "B-01")!.Quantity);
        Assert.Single(inventory.GetTransactions().Where(item => item.Type == InventoryTransactionType.Move));
        Assert.All(first.ResourceLocks, item => Assert.NotNull(item.ReleasedAt));
    }

    [Fact]
    public async Task Unknown_device_result_does_not_move_inventory_or_release_locks()
    {
        var source = Guid.NewGuid();
        var destination = Guid.NewGuid();
        var material = Guid.NewGuid();
        var pallet = Guid.NewGuid();
        var inventory = new InventoryService();
        await inventory.IncreaseAsync(material, pallet, source, null, 3m, 3m, new InventoryOperationContext("seed-unknown"));
        var service = CreateService(inventory, DeviceOperationStatus.Accepted);
        var request = new RelocationRequest("relocate-004", material, pallet, source, destination, 3m, 3m, "PLC-01");
        var submitted = await service.SubmitAsync(request);

        var result = await service.ProcessResultAsync(request.IdempotencyKey, DeviceOperationStatus.PhysicalStateUnknown);

        Assert.Equal(RelocationStatus.PhysicalStateUnknown, result.Status);
        Assert.Null(inventory.GetBalance(material, pallet, destination, null));
        Assert.All(submitted.ResourceLocks, item => Assert.Null(item.ReleasedAt));
    }

    [Fact]
    public async Task Active_source_resource_lock_rejects_second_relocation()
    {
        var source = Guid.NewGuid();
        var destination = Guid.NewGuid();
        var material = Guid.NewGuid();
        var pallet = Guid.NewGuid();
        var inventory = new InventoryService();
        await inventory.IncreaseAsync(material, pallet, source, null, 2m, 2m, new InventoryOperationContext("seed-lock"));
        var service = CreateService(inventory, DeviceOperationStatus.Accepted);
        var first = new RelocationRequest("relocate-lock-1", material, pallet, source, destination, 1m, 1m, "PLC-01");
        await service.SubmitAsync(first);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SubmitAsync(
            first with { IdempotencyKey = "relocate-lock-2", DestinationLocationId = Guid.NewGuid() }));
    }

    [Fact]
    public async Task Cross_device_relocation_is_rejected_without_confirmed_capability()
    {
        var inventory = new InventoryService();
        var service = CreateService(inventory);
        var request = new RelocationRequest(
            "relocate-cross-device", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1m, 1m, "PLC-02",
            SourceDeviceId: "PLC-01", DestinationDeviceId: "PLC-02");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SubmitAsync(request));
    }

    private static RelocationService CreateService(InventoryService inventory, DeviceOperationStatus status = DeviceOperationStatus.Offline)
        => new(inventory, new WmsTaskScheduler(new ScenarioGateway(status)));

    private sealed class ScenarioGateway(DeviceOperationStatus status) : IWarehouseDeviceGateway
    {
        public Task<DeviceOperationResult> SubmitInboundAsync(DeviceTask task, CancellationToken cancellationToken = default) => Submit(task);
        public Task<DeviceOperationResult> SubmitOutboundAsync(DeviceTask task, CancellationToken cancellationToken = default) => Submit(task);
        public Task<DeviceOperationResult> SubmitTransferAsync(DeviceTask task, CancellationToken cancellationToken = default) => Submit(task);
        public Task<DeviceResultObservation?> GetStatusAsync(string deviceTaskNumber, CancellationToken cancellationToken = default) => Task.FromResult<DeviceResultObservation?>(null);
        public Task<DeviceOperationResult> TestConnectionAsync(string deviceId, CancellationToken cancellationToken = default) => Task.FromResult(new DeviceOperationResult($"connection:{deviceId}", DeviceOperationStatus.Succeeded));
        public Task<DeviceOperationResult> RequestStopAsync(DeviceTask task, CancellationToken cancellationToken = default) => Task.FromResult(new DeviceOperationResult(task.IdempotencyKey, DeviceOperationStatus.StopConfirmed));
        private Task<DeviceOperationResult> Submit(DeviceTask task) => Task.FromResult(new DeviceOperationResult(task.IdempotencyKey, status, $"sim-{task.WmsTaskId}"));
    }
}
