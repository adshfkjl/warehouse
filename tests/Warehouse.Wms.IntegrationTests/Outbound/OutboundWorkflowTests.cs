using Warehouse.Wms.Application.Devices;
using Warehouse.Wms.Application.Inventory;
using Warehouse.Wms.Application.Outbound;
using Warehouse.Wms.Application.Tasks;
using Warehouse.Wms.Domain.Devices;
using Warehouse.Wms.Domain.Inventory;
using Warehouse.Wms.Domain.MasterData;
using Warehouse.Wms.Domain.Outbound;
using Warehouse.Wms.Domain.Tasks;
using WmsTaskScheduler = Warehouse.Wms.Application.Tasks.TaskScheduler;

namespace Warehouse.Wms.IntegrationTests.Outbound;

public sealed class OutboundWorkflowTests
{
    [Fact]
    public async Task Accepted_device_result_does_not_decrease_inventory_until_review_passes()
    {
        var fixture = CreateFixture(DeviceOperationStatus.Accepted);
        var allocation = fixture.Allocate("outbound-alloc-001");
        var submitted = await fixture.Tasks.SubmitAsync(allocation,
            new OutboundTaskRequest("outbound-task-001", "PLC-01", fixture.LoadingPoint.Id));

        Assert.Equal(TaskState.SentToPlc, submitted.Task.State);
        Assert.Equal(5m, fixture.Inventory.GetBalance(fixture.MaterialId, fixture.PalletId, fixture.LocationId, null)!.Quantity);

        await fixture.Review.ProcessDeviceResultAsync(submitted.Task.TaskNumber, DeviceOperationStatus.Succeeded);
        var reviewed = await fixture.Review.ReviewAsync(submitted.Task.TaskNumber,
            new OutboundReviewRequest(fixture.PalletId.ToString("D"), 10m, true));

        Assert.Equal(OutboundReviewStatus.Completed, reviewed.Status);
        Assert.Equal(3m, fixture.Inventory.GetBalance(fixture.MaterialId, fixture.PalletId, fixture.LocationId, null)!.Quantity);
        Assert.Single(fixture.Inventory.GetTransactions().Where(item => item.Type == InventoryTransactionType.Decrease));
        Assert.All(submitted.ResourceLocks, item => Assert.NotNull(item.ReleasedAt));
    }

    [Fact]
    public async Task Pallet_or_weight_mismatch_enters_exception_without_decreasing_inventory()
    {
        var fixture = CreateFixture(DeviceOperationStatus.Accepted);
        var allocation = fixture.Allocate("outbound-alloc-002");
        var submitted = await fixture.Tasks.SubmitAsync(allocation,
            new OutboundTaskRequest("outbound-task-002", "PLC-01", fixture.LoadingPoint.Id));
        await fixture.Review.ProcessDeviceResultAsync(submitted.Task.TaskNumber, DeviceOperationStatus.Succeeded);

        var reviewed = await fixture.Review.ReviewAsync(submitted.Task.TaskNumber,
            new OutboundReviewRequest("WRONG-PALLET", 10m, true));

        Assert.Equal(OutboundReviewStatus.Exception, reviewed.Status);
        Assert.Equal(5m, fixture.Inventory.GetBalance(fixture.MaterialId, fixture.PalletId, fixture.LocationId, null)!.Quantity);
        Assert.All(submitted.ResourceLocks, item => Assert.Null(item.ReleasedAt));
    }

    [Fact]
    public async Task Device_failure_or_unknown_result_does_not_decrease_inventory()
    {
        var failed = CreateFixture(DeviceOperationStatus.Offline);
        var failedAllocation = failed.Allocate("outbound-alloc-003");
        var failedTask = await failed.Tasks.SubmitAsync(failedAllocation,
            new OutboundTaskRequest("outbound-task-003", "PLC-01", failed.LoadingPoint.Id));
        Assert.Equal(OutboundReviewStatus.Exception,
            (await failed.Review.ProcessDeviceResultAsync(failedTask.Task.TaskNumber, DeviceOperationStatus.Failed)).Status);
        Assert.Equal(5m, failed.Inventory.GetBalance(failed.MaterialId, failed.PalletId, failed.LocationId, null)!.Quantity);

        var unknown = CreateFixture(DeviceOperationStatus.Accepted);
        var unknownAllocation = unknown.Allocate("outbound-alloc-004");
        var unknownTask = await unknown.Tasks.SubmitAsync(unknownAllocation,
            new OutboundTaskRequest("outbound-task-004", "PLC-01", unknown.LoadingPoint.Id));
        Assert.Equal(OutboundReviewStatus.PhysicalStateUnknown,
            (await unknown.Review.ProcessDeviceResultAsync(unknownTask.Task.TaskNumber, DeviceOperationStatus.PhysicalStateUnknown)).Status);
        Assert.Equal(5m, unknown.Inventory.GetBalance(unknown.MaterialId, unknown.PalletId, unknown.LocationId, null)!.Quantity);
    }

    [Fact]
    public async Task Review_replay_is_idempotent()
    {
        var fixture = CreateFixture(DeviceOperationStatus.Accepted);
        var allocation = fixture.Allocate("outbound-alloc-005");
        var submitted = await fixture.Tasks.SubmitAsync(allocation,
            new OutboundTaskRequest("outbound-task-005", "PLC-01", fixture.LoadingPoint.Id));
        await fixture.Review.ProcessDeviceResultAsync(submitted.Task.TaskNumber, DeviceOperationStatus.Succeeded);
        var request = new OutboundReviewRequest(fixture.PalletId.ToString("D"), 10m, true);

        var first = await fixture.Review.ReviewAsync(submitted.Task.TaskNumber, request);
        var second = await fixture.Review.ReviewAsync(submitted.Task.TaskNumber, request);

        Assert.Equal(OutboundReviewStatus.Completed, first.Status);
        Assert.Equal(OutboundReviewStatus.AlreadyCompleted, second.Status);
        Assert.Single(fixture.Inventory.GetTransactions().Where(item => item.Type == InventoryTransactionType.Decrease));
    }

    [Fact]
    public async Task Occupied_loading_point_is_rejected_before_device_submission()
    {
        var fixture = CreateFixture(DeviceOperationStatus.Accepted, loadingPointOccupied: true);
        var allocation = fixture.Allocate("outbound-alloc-006");

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Tasks.SubmitAsync(
            allocation,
            new OutboundTaskRequest("outbound-task-006", "PLC-01", fixture.LoadingPoint.Id)));
    }

    private static Fixture CreateFixture(DeviceOperationStatus status, bool loadingPointOccupied = false)
    {
        var materialId = Guid.NewGuid();
        var palletId = Guid.NewGuid();
        var locationId = Guid.NewGuid();
        var inventory = new InventoryService();
        inventory.IncreaseAsync(materialId, palletId, locationId, null, 5m, 25m,
            new InventoryOperationContext("seed-outbound")).GetAwaiter().GetResult();
        var allocationService = new OutboundAllocationService(inventory);
        var order = allocationService.Create("OB-WORKFLOW-001");
        order.AddLine(new OutboundLine(materialId, 2m, palletId: palletId, locationId: locationId));
        var loadingPoint = new LoadingPoint("LP-OUT-01", "出库口");
        var gateway = new ScenarioGateway(status);
        var scheduler = new WmsTaskScheduler(gateway);
        var tasks = new OutboundTaskService(allocationService, scheduler, [new OutboundLoadingPoint(loadingPoint, loadingPointOccupied)]);
        var review = new OutboundReviewService(tasks, inventory);
        return new Fixture(materialId, palletId, locationId, new Location(locationId, "A-OUT-01", 1, 100m, 1000m, 1000m, 1000m), loadingPoint, inventory, allocationService, order, tasks, review);
    }

    private sealed record Fixture(Guid MaterialId, Guid PalletId, Guid LocationId, Location Location, LoadingPoint LoadingPoint, InventoryService Inventory, OutboundAllocationService Allocations, OutboundOrder Order, OutboundTaskService Tasks, OutboundReviewService Review)
    {
        public OutboundAllocation Allocate(string key)
            => Allocations.Allocate(new OutboundAllocationRequest(key, Order.OrderNumber, Order.Lines.Single().Id, 2m, PalletId: PalletId));
    }

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
