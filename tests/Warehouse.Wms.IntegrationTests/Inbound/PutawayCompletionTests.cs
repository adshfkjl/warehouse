using Warehouse.Wms.Application.Devices;
using Warehouse.Wms.Application.Inbound;
using Warehouse.Wms.Application.Inventory;
using Warehouse.Wms.Application.Tasks;
using Warehouse.Wms.Domain.Devices;
using Warehouse.Wms.Domain.Inventory;
using Warehouse.Wms.Domain.MasterData;
using WmsTaskScheduler = Warehouse.Wms.Application.Tasks.TaskScheduler;

namespace Warehouse.Wms.IntegrationTests.Inbound;

public sealed class PutawayCompletionTests
{
    [Fact]
    public async Task Success_posts_inventory_once_and_releases_resources()
    {
        var fixture = CreateFixture(DeviceOperationStatus.Accepted, 10m);

        var submitted = await fixture.Putaway.SubmitAsync(fixture.Pending,
            new PutawayTaskRequest("putaway-complete-001", "PLC-01", fixture.LoadingPoint.Id));

        var first = await fixture.Reconciliation.CompleteAsync(submitted.Task.TaskNumber);
        var second = await fixture.Reconciliation.CompleteAsync(submitted.Task.TaskNumber);

        Assert.Equal(PutawayReconciliationStatus.Completed, first.Status);
        Assert.Equal(PutawayReconciliationStatus.AlreadyCompleted, second.Status);
        Assert.Single(fixture.Inventory.GetTransactions());
        Assert.Equal(
            (1m, 10m),
            (fixture.Inventory.GetBalance(fixture.MaterialId, fixture.PalletId, fixture.Location.Id, null)!.Quantity,
                fixture.Inventory.GetBalance(fixture.MaterialId, fixture.PalletId, fixture.Location.Id, null)!.WeightKg));
        Assert.All(submitted.ResourceLocks, item => Assert.NotNull(item.ReleasedAt));
    }

    [Fact]
    public async Task Failure_keeps_pending_inventory_and_resource_locks()
    {
        var fixture = CreateFixture(DeviceOperationStatus.Offline, 10m);
        var submitted = await fixture.Putaway.SubmitAsync(fixture.Pending,
            new PutawayTaskRequest("putaway-complete-002", "PLC-01", fixture.LoadingPoint.Id));

        var result = await fixture.Reconciliation.ProcessResultAsync(
            submitted.Task.TaskNumber, DeviceOperationStatus.Failed);

        Assert.Equal(PutawayReconciliationStatus.Exception, result.Status);
        Assert.Null(fixture.Inventory.GetBalance(fixture.MaterialId, fixture.PalletId, fixture.Location.Id, null));
        Assert.All(submitted.ResourceLocks, item => Assert.Null(item.ReleasedAt));
    }

    [Fact]
    public async Task Unknown_result_does_not_post_or_release_resources()
    {
        var fixture = CreateFixture(DeviceOperationStatus.Accepted, 10m);
        var submitted = await fixture.Putaway.SubmitAsync(fixture.Pending,
            new PutawayTaskRequest("putaway-complete-003", "PLC-01", fixture.LoadingPoint.Id));

        var result = await fixture.Reconciliation.ProcessResultAsync(
            submitted.Task.TaskNumber, DeviceOperationStatus.PhysicalStateUnknown);

        Assert.Equal(PutawayReconciliationStatus.PhysicalStateUnknown, result.Status);
        Assert.Empty(fixture.Inventory.GetTransactions());
        Assert.All(submitted.ResourceLocks, item => Assert.Null(item.ReleasedAt));
    }

    private static Fixture CreateFixture(DeviceOperationStatus submissionStatus, decimal weightKg)
    {
        var materialId = Guid.NewGuid();
        var palletId = Guid.NewGuid();
        var inbound = new InboundOrderService();
        var order = inbound.Create("IB-COMPLETE-001", [new InboundLineRequest(materialId, 1m)]);
        var receipt = inbound.Receive(order.OrderNumber, new InboundReceiptRequest(
            "receipt-complete-001", 1m, weightKg, PalletId: palletId));
        var location = new Location(Guid.NewGuid(), "A-COMPLETE", 1, 100m, 1000m, 1000m, 1000m);
        var loadingPoint = new LoadingPoint("LP-COMPLETE", "入库口");
        var allocation = new PutawayAllocationService(
            [new PutawayLocationCandidate(location)],
            [new PutawayLoadingPointCandidate(loadingPoint, HasPallet: true)]);
        var scheduler = new WmsTaskScheduler(new ScenarioGateway(submissionStatus));
        var putaway = new PutawayTaskService(inbound, allocation, scheduler);
        return new Fixture(
            materialId,
            palletId,
            location,
            loadingPoint,
            receipt.PendingInbound,
            putaway,
            new InventoryService(),
            inbound,
            allocation);
    }

    private sealed record Fixture(
        Guid MaterialId,
        Guid PalletId,
        Location Location,
        LoadingPoint LoadingPoint,
        PendingInboundInventory Pending,
        PutawayTaskService Putaway,
        InventoryService Inventory,
        InboundOrderService Inbound,
        PutawayAllocationService Allocation)
    {
        private InboundReconciliationService? _reconciliation;

        public InboundReconciliationService Reconciliation
            => _reconciliation ??= new(Inbound, Putaway, Inventory);
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
