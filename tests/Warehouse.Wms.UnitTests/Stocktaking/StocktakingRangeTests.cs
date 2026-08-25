using Warehouse.Wms.Application.Stocktaking;
using Warehouse.Wms.Domain.Inventory;
using Warehouse.Wms.Domain.Stocktaking;
using Warehouse.Wms.Application.Devices;
using Warehouse.Wms.Application.Tasks;
using Warehouse.Wms.Domain.Devices;
using WmsTaskScheduler = Warehouse.Wms.Application.Tasks.TaskScheduler;
using Warehouse.Wms.Domain.Tasks;

namespace Warehouse.Wms.UnitTests.Stocktaking;

public sealed class StocktakingRangeTests
{
    [Fact]
    public void Supports_full_zone_material_batch_and_pallet_filters_with_deterministic_order()
    {
        var service = new StocktakingService([
            Item("A1-02", "Z1", "M-02", "B2", "P-02", 2m),
            Item("A1-01", "Z1", "M-01", "B1", "P-01", 1m),
            Item("A2-01", "Z2", "M-01", "B1", "P-03", 3m)]);

        var result = service.Create(new StocktakingRequest(
            "ST-001",
            ZoneCode: "Z1",
            MaterialCode: "M-01",
            BatchNumber: "B1",
            PalletCode: "P-01"));

        Assert.Single(result.Items);
        Assert.Equal("A1-01", result.Items[0].LocationCode);
        Assert.Equal(StocktakingState.Draft, result.State);
    }

    [Fact]
    public void Parses_legacy_A1_to_A8_range_and_prefix_number_closed_interval()
    {
        var service = new StocktakingService([
            Item("A1-01", "Z1", "M", null, "P-01", 1m),
            Item("A4-02", "Z1", "M", null, "P-02", 1m),
            Item("A8-99", "Z1", "M", null, "P-03", 1m),
            Item("A9-01", "Z1", "M", null, "P-04", 1m)]);

        var result = service.Create(new StocktakingRequest(
            "ST-002", LocationRangeStart: "A1", LocationRangeEnd: "A8"));

        Assert.Equal(["A1-01", "A4-02", "A8-99"], result.Items.Select(item => item.LocationCode));
    }

    [Fact]
    public void Rejects_empty_range_and_duplicate_task_number()
    {
        var service = new StocktakingService([Item("A1-01", "Z1", "M", null, "P-01", 1m)]);
        Assert.Throws<InvalidOperationException>(() => service.Create(new StocktakingRequest("ST-003", ZoneCode: "UNKNOWN")));
        service.Create(new StocktakingRequest("ST-004"));
        Assert.Throws<InvalidOperationException>(() => service.Create(new StocktakingRequest("ST-004")));
    }

    [Fact]
    public void Records_actual_count_and_preserves_difference_for_approval()
    {
        var service = new StocktakingService([Item("A1-01", "Z1", "M", null, "P-01", 1m)]);
        var task = service.Create(new StocktakingRequest("ST-005"));
        service.Start(task.TaskNumber);
        service.RecordCount(task.TaskNumber, task.Items.Single().Id, 0m, 0m, "LP-01");

        var completed = service.Complete(task.TaskNumber);

        Assert.Equal(StocktakingState.CompletedWithErrors, completed.State);
        Assert.Equal(StocktakingItemState.Difference, completed.Items.Single().State);
        Assert.Equal("LP-01", completed.Items.Single().LoadingPointCode);
    }

    [Fact]
    public async Task Device_assisted_counting_queues_through_shared_scheduler_and_serializes_loading_point()
    {
        var gateway = new ScenarioGateway();
        var service = new StocktakingService(
            [Item("A1-01", "Z1", "M", null, "P-01", 1m)],
            new WmsTaskScheduler(gateway));
        var task = service.Create(new StocktakingRequest("ST-006"));
        service.Start(task.TaskNumber);

        var deviceTask = await service.QueueDeviceTaskAsync(task.TaskNumber, task.Items.Single().Id, "PLC-01", "LP-01");

        Assert.Equal(TaskState.Queued, deviceTask.Task.State);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.QueueDeviceTaskAsync(
            task.TaskNumber, Guid.NewGuid(), "PLC-01", "LP-01"));
    }

    private static StocktakingInventoryItem Item(string location, string zone, string material, string? batch, string pallet, decimal quantity)
        => new(location, zone, material, batch, pallet, Guid.NewGuid(), Guid.NewGuid(), quantity, quantity, InventoryStatus.Available);

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
