using Warehouse.Wms.Application.Devices;
using Warehouse.Wms.Application.Inbound;
using Warehouse.Wms.Application.Tasks;
using Warehouse.Wms.Domain.Devices;
using Warehouse.Wms.Domain.Inventory;
using Warehouse.Wms.Domain.MasterData;
using Warehouse.Wms.Domain.Tasks;
using WmsTaskScheduler = Warehouse.Wms.Application.Tasks.TaskScheduler;

namespace Warehouse.Wms.IntegrationTests.Inbound;

public sealed class InboundApiTests
{
    [Fact]
    public async Task Simulated_gateway_submission_keeps_inventory_pending_until_completion_task()
    {
        var inbound = new InboundOrderService();
        var order = inbound.Create("IB-API-001", [new InboundLineRequest(Guid.NewGuid(), 1m)]);
        var receipt = await inbound.ReceiveAsync(order.OrderNumber,
            new InboundReceiptRequest("receipt-api-001", 1m, 10m, PalletCode: "PALLET-API-001"));
        var location = new Location(Guid.NewGuid(), "A-01", 1, 100m, 1000m, 1000m, 1000m);
        var loadingPoint = new LoadingPoint("LP-01", "入库口");
        var allocation = new PutawayAllocationService(
            [new PutawayLocationCandidate(location)],
            [new PutawayLoadingPointCandidate(loadingPoint, HasPallet: true)]);
        var gateway = new ScenarioGateway(DeviceOperationStatus.Accepted);
        var scheduler = new WmsTaskScheduler(gateway);
        var service = new PutawayTaskService(inbound, allocation, scheduler);

        var result = await service.SubmitAsync(receipt.PendingInbound, new PutawayTaskRequest(
            "putaway-api-001", "PLC-01", loadingPoint.Id, "v1"));

        Assert.Equal(TaskState.SentToPlc, result.Task.State);
        Assert.Equal(InventoryStatus.PendingInbound, receipt.PendingInbound.Status);
        Assert.Null(receipt.PendingInbound.LocationId);
        Assert.NotNull(result.DispatchResult);
        Assert.Equal(DeviceOperationStatus.Accepted, result.DispatchResult!.Status);
        Assert.Equal(
            ["LoadingPoint", "Location", "Pallet", "PendingInboundInventory"],
            result.ResourceLocks.Select(item => item.ResourceType).OrderBy(item => item, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Offline_device_result_is_exposed_and_duplicate_submission_is_idempotent()
    {
        var inbound = new InboundOrderService();
        var order = inbound.Create("IB-API-002", [new InboundLineRequest(Guid.NewGuid(), 1m)]);
        var receipt = await inbound.ReceiveAsync(order.OrderNumber,
            new InboundReceiptRequest("receipt-api-002", 1m, 10m, PalletCode: "PALLET-API-002"));
        var location = new Location(Guid.NewGuid(), "A-02", 1, 100m, 1000m, 1000m, 1000m);
        var loadingPoint = new LoadingPoint("LP-02", "入库口");
        var allocation = new PutawayAllocationService(
            [new PutawayLocationCandidate(location)],
            [new PutawayLoadingPointCandidate(loadingPoint, HasPallet: true)]);
        var gateway = new ScenarioGateway(DeviceOperationStatus.Offline);
        var scheduler = new WmsTaskScheduler(gateway);
        var service = new PutawayTaskService(inbound, allocation, scheduler);
        var request = new PutawayTaskRequest("putaway-api-002", "PLC-01", loadingPoint.Id, "v1", MaxAttempts: 1);

        var first = await service.SubmitAsync(receipt.PendingInbound, request);
        var second = await service.SubmitAsync(receipt.PendingInbound, request);

        Assert.Same(first, second);
        Assert.Equal(TaskState.Failed, first.Task.State);
        Assert.Equal(InventoryStatus.PendingInbound, receipt.PendingInbound.Status);
    }

    [Fact]
    public async Task Loading_point_without_pallet_is_rejected_before_device_submission()
    {
        var inbound = new InboundOrderService();
        var order = inbound.Create("IB-API-003", [new InboundLineRequest(Guid.NewGuid(), 1m)]);
        var receipt = await inbound.ReceiveAsync(order.OrderNumber,
            new InboundReceiptRequest("receipt-api-003", 1m, 10m, PalletCode: "PALLET-API-003"));
        var location = new Location(Guid.NewGuid(), "A-03", 1, 100m, 1000m, 1000m, 1000m);
        var loadingPoint = new LoadingPoint("LP-03", "入库口");
        var allocation = new PutawayAllocationService(
            [new PutawayLocationCandidate(location)],
            [new PutawayLoadingPointCandidate(loadingPoint, HasPallet: false)]);
        var scheduler = new WmsTaskScheduler(new ScenarioGateway(DeviceOperationStatus.Accepted));
        var service = new PutawayTaskService(inbound, allocation, scheduler);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SubmitAsync(
            receipt.PendingInbound,
            new PutawayTaskRequest("putaway-api-003", "PLC-01", loadingPoint.Id, "v1")));
        Assert.Empty(allocation.Allocations);
    }

    private sealed class ScenarioGateway(DeviceOperationStatus submissionStatus) : IWarehouseDeviceGateway
    {
        public Task<DeviceOperationResult> SubmitInboundAsync(DeviceTask task, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeviceOperationResult(task.IdempotencyKey, submissionStatus, $"sim-{task.WmsTaskId}"));

        public Task<DeviceOperationResult> SubmitOutboundAsync(DeviceTask task, CancellationToken cancellationToken = default)
            => SubmitInboundAsync(task, cancellationToken);

        public Task<DeviceOperationResult> SubmitTransferAsync(DeviceTask task, CancellationToken cancellationToken = default)
            => SubmitInboundAsync(task, cancellationToken);

        public Task<DeviceResultObservation?> GetStatusAsync(string deviceTaskNumber, CancellationToken cancellationToken = default)
            => Task.FromResult<DeviceResultObservation?>(null);

        public Task<DeviceOperationResult> TestConnectionAsync(string deviceId, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeviceOperationResult($"connection:{deviceId}", DeviceOperationStatus.Succeeded));

        public Task<DeviceOperationResult> RequestStopAsync(DeviceTask task, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeviceOperationResult(task.IdempotencyKey, DeviceOperationStatus.StopConfirmed));
    }
}
