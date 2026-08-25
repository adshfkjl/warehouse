using Warehouse.Wms.Application.Devices;
using Warehouse.Wms.Application.Tasks;
using Warehouse.Wms.Domain.Devices;
using Warehouse.Wms.Domain.Tasks;
using WmsTaskScheduler = Warehouse.Wms.Application.Tasks.TaskScheduler;

namespace Warehouse.Wms.UnitTests.Tasks;

public sealed class TaskSchedulerTests
{
    private static readonly string[] ExpectedDispatchOrder = ["high", "low"];

    [Fact]
    public async Task Scheduler_serializes_same_device_and_honors_priority()
    {
        var gateway = new FakeGateway(DeviceOperationStatus.Accepted);
        var scheduler = new WmsTaskScheduler(gateway);
        var low = Request("low", "PLC-01", priority: 1);
        var high = Request("high", "PLC-01", priority: 10);

        await scheduler.EnqueueAsync(low);
        await scheduler.EnqueueAsync(high);

        var first = await scheduler.DispatchNextAsync();
        Assert.NotNull(first);
        Assert.Equal("high", first!.Request.Task.TaskNumber);
        Assert.Null(await scheduler.DispatchNextAsync());

        var processor = new DeviceResultProcessor(scheduler);
        await processor.ProcessAsync(
            first.Request,
            new DeviceResultObservation(first.DeviceTaskNumber!, 1, DeviceOperationStatus.Succeeded, DeviceObservationSource.Polling, DateTimeOffset.UtcNow));

        var second = await scheduler.DispatchNextAsync();
        Assert.NotNull(second);
        Assert.Equal("low", second!.Request.Task.TaskNumber);
        Assert.Equal(ExpectedDispatchOrder, gateway.SubmittedTaskNumbers);
    }

    [Fact]
    public async Task Physical_unknown_submission_is_not_retried_without_device_query_capability()
    {
        var gateway = new FakeGateway(DeviceOperationStatus.PhysicalStateUnknown);
        var scheduler = new WmsTaskScheduler(gateway, capabilities: DeviceCapability.None);
        var request = Request("unknown", "PLC-01");

        await scheduler.EnqueueAsync(request);
        var result = await scheduler.DispatchNextAsync();
        Assert.NotNull(result);

        Assert.Equal(DeviceOperationStatus.PhysicalStateUnknown, result!.Status);
        Assert.Equal(TaskState.PhysicalStateUnknown, request.Task.State);
        Assert.Null(await scheduler.DispatchNextAsync());
        Assert.Single(gateway.SubmittedTaskNumbers);
    }

    [Fact]
    public async Task Failed_submission_is_not_retried_when_task_query_capability_is_missing()
    {
        var gateway = new FakeGateway(DeviceOperationStatus.Failed);
        var scheduler = new WmsTaskScheduler(
            gateway,
            capabilities: DeviceCapability.TaskKeyDeduplication);
        var request = Request("failed", "PLC-01");

        await scheduler.EnqueueAsync(request);
        var result = await scheduler.DispatchNextAsync();

        Assert.NotNull(result);
        Assert.Equal(DeviceOperationStatus.Failed, result!.Status);
        Assert.Equal(TaskState.Failed, request.Task.State);
        Assert.Single(gateway.SubmittedTaskNumbers);
    }

    [Fact]
    public async Task Result_processor_deduplicates_polling_and_callback_observations()
    {
        var gateway = new FakeGateway(DeviceOperationStatus.Accepted);
        var scheduler = new WmsTaskScheduler(gateway);
        var request = Request("result", "PLC-01");
        await scheduler.EnqueueAsync(request);
        var dispatch = await scheduler.DispatchNextAsync();
        Assert.NotNull(dispatch);
        var processor = new DeviceResultProcessor(scheduler);
        var observedAt = DateTimeOffset.UtcNow;

        Assert.True(await processor.ProcessAsync(request, new DeviceResultObservation(
            dispatch!.DeviceTaskNumber!, 2, DeviceOperationStatus.Succeeded, DeviceObservationSource.Polling, observedAt)));
        Assert.False(await processor.ProcessAsync(request, new DeviceResultObservation(
            dispatch.DeviceTaskNumber!, 2, DeviceOperationStatus.Succeeded, DeviceObservationSource.Callback, observedAt.AddSeconds(1))));
        Assert.Equal(TaskState.Succeeded, request.Task.State);
    }

    [Fact]
    public async Task Result_processor_rejects_observation_for_another_device_task()
    {
        var gateway = new FakeGateway(DeviceOperationStatus.Accepted);
        var scheduler = new WmsTaskScheduler(gateway);
        var request = Request("result-mismatch", "PLC-01");
        await scheduler.EnqueueAsync(request);
        var dispatch = await scheduler.DispatchNextAsync();
        Assert.NotNull(dispatch);

        var processor = new DeviceResultProcessor(scheduler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => processor.ProcessAsync(
            request,
            new DeviceResultObservation("different-device-task", 1, DeviceOperationStatus.Succeeded,
                DeviceObservationSource.Polling, DateTimeOffset.UtcNow)));
    }

    [Fact]
    public async Task Result_processor_ignores_older_observation_versions()
    {
        var gateway = new FakeGateway(DeviceOperationStatus.Accepted);
        var scheduler = new WmsTaskScheduler(gateway);
        var request = Request("result-version", "PLC-01");
        await scheduler.EnqueueAsync(request);
        var dispatch = await scheduler.DispatchNextAsync();
        Assert.NotNull(dispatch);

        var processor = new DeviceResultProcessor(scheduler);
        var observedAt = DateTimeOffset.UtcNow;
        Assert.True(await processor.ProcessAsync(request, new DeviceResultObservation(
            dispatch!.DeviceTaskNumber!, 2, DeviceOperationStatus.Executing, DeviceObservationSource.Polling, observedAt)));
        Assert.False(await processor.ProcessAsync(request, new DeviceResultObservation(
            dispatch.DeviceTaskNumber!, 1, DeviceOperationStatus.Failed, DeviceObservationSource.Callback, observedAt.AddSeconds(1))));
        Assert.Equal(TaskState.Executing, request.Task.State);
    }

    [Fact]
    public async Task Submission_response_with_wrong_idempotency_key_enters_physical_unknown()
    {
        var gateway = new FakeGateway(DeviceOperationStatus.Accepted, responseIdempotencyKey: "wrong-key");
        var scheduler = new WmsTaskScheduler(gateway);
        var request = Request("response-mismatch", "PLC-01");

        await scheduler.EnqueueAsync(request);
        var result = await scheduler.DispatchNextAsync();

        Assert.NotNull(result);
        Assert.Equal(DeviceOperationStatus.PhysicalStateUnknown, result!.Status);
        Assert.Equal(TaskState.PhysicalStateUnknown, request.Task.State);
    }

    [Fact]
    public async Task Unknown_device_observation_enters_physical_unknown()
    {
        var gateway = new FakeGateway(DeviceOperationStatus.Accepted);
        var scheduler = new WmsTaskScheduler(gateway);
        var request = Request("observation-unknown", "PLC-01");
        await scheduler.EnqueueAsync(request);
        var dispatch = await scheduler.DispatchNextAsync();
        Assert.NotNull(dispatch);

        var processor = new DeviceResultProcessor(scheduler);
        await processor.ProcessAsync(request, new DeviceResultObservation(
            dispatch!.DeviceTaskNumber!, 1, DeviceOperationStatus.Unknown,
            DeviceObservationSource.Polling, DateTimeOffset.UtcNow));

        Assert.Equal(TaskState.PhysicalStateUnknown, request.Task.State);
    }

    private static TaskDispatchRequest Request(string number, string deviceId, int priority = 0) =>
        new(
            new WarehouseTask(number, "Putaway"),
            new DeviceTask($"idem-{number}", number, deviceId, "1-1", "2-1", "0", "v1"),
            DeviceOperationKind.Inbound,
            priority);

    private sealed class FakeGateway(
        DeviceOperationStatus submissionStatus,
        string? responseIdempotencyKey = null) : IWarehouseDeviceGateway
    {
        public List<string> SubmittedTaskNumbers { get; } = [];

        public Task<DeviceOperationResult> SubmitInboundAsync(DeviceTask task, CancellationToken cancellationToken = default)
        {
            SubmittedTaskNumbers.Add(task.WmsTaskId);
            return Task.FromResult(new DeviceOperationResult(responseIdempotencyKey ?? task.IdempotencyKey, submissionStatus,
                submissionStatus == DeviceOperationStatus.Accepted ? $"device-{task.WmsTaskId}" : null));
        }

        public Task<DeviceOperationResult> SubmitOutboundAsync(DeviceTask task, CancellationToken cancellationToken = default) => SubmitInboundAsync(task, cancellationToken);

        public Task<DeviceOperationResult> SubmitTransferAsync(DeviceTask task, CancellationToken cancellationToken = default) => SubmitInboundAsync(task, cancellationToken);

        public Task<DeviceResultObservation?> GetStatusAsync(string deviceTaskNumber, CancellationToken cancellationToken = default) =>
            Task.FromResult<DeviceResultObservation?>(null);

        public Task<DeviceOperationResult> TestConnectionAsync(string deviceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DeviceOperationResult($"connection:{deviceId}", DeviceOperationStatus.Succeeded));

        public Task<DeviceOperationResult> RequestStopAsync(DeviceTask task, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DeviceOperationResult(task.IdempotencyKey, DeviceOperationStatus.StopConfirmed));
    }
}
