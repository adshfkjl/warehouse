using Warehouse.Wms.Application.Devices;
using Warehouse.Wms.Application.Tasks;
using Warehouse.Wms.Domain.Devices;
using Warehouse.Wms.Domain.Tasks;
using Warehouse.Wms.Infrastructure.Background;
using WmsTaskScheduler = Warehouse.Wms.Application.Tasks.TaskScheduler;

namespace Warehouse.Wms.IntegrationTests.Tasks;

public sealed class TaskRecoveryTests
{
    [Fact]
    public async Task Worker_restart_queries_existing_device_task_instead_of_resubmitting()
    {
        var gateway = new RecoveryGateway(DeviceCapability.TaskQuery, DeviceOperationStatus.Executing);
        var state = new TaskSchedulerState();
        var firstScheduler = new WmsTaskScheduler(gateway, state: state);
        var request = new TaskDispatchRequest(
            new WarehouseTask("recover-001", "Putaway"),
            new DeviceTask("recover-idem", "recover-001", "PLC-01", "1-1", "2-1", "0", "v1"),
            DeviceOperationKind.Inbound);

        await firstScheduler.EnqueueAsync(request);
        var sent = await firstScheduler.DispatchNextAsync();
        Assert.NotNull(sent);
        Assert.Equal(1, gateway.SubmissionCount);

        var restartedScheduler = new WmsTaskScheduler(gateway, state: state);
        var worker = new TaskWorker(restartedScheduler);
        await worker.RunOnceAsync();

        Assert.Equal(1, gateway.SubmissionCount);
        Assert.Equal(TaskState.Executing, request.Task.State);
        Assert.Equal(sent!.DeviceTaskNumber, request.DeviceTaskNumber);
    }

    [Fact]
    public async Task Worker_restart_without_query_capability_marks_task_unknown_without_resubmitting()
    {
        var gateway = new RecoveryGateway(DeviceCapability.None, DeviceOperationStatus.Accepted);
        var state = new TaskSchedulerState();
        var scheduler = new WmsTaskScheduler(gateway, capabilities: DeviceCapability.None, state: state);
        var request = new TaskDispatchRequest(
            new WarehouseTask("recover-002", "Putaway"),
            new DeviceTask("recover-idem-2", "recover-002", "PLC-01", "1-1", "2-1", "0", "v1"),
            DeviceOperationKind.Inbound);

        await scheduler.EnqueueAsync(request);
        await scheduler.DispatchNextAsync();
        var worker = new TaskWorker(new WmsTaskScheduler(gateway, capabilities: DeviceCapability.None, state: state));
        await worker.RunOnceAsync();

        Assert.Equal(1, gateway.SubmissionCount);
        Assert.Equal(TaskState.PhysicalStateUnknown, request.Task.State);
    }

    private sealed class RecoveryGateway(DeviceCapability capabilities, DeviceOperationStatus status) : IWarehouseDeviceGateway
    {
        public int SubmissionCount { get; private set; }

        public Task<DeviceOperationResult> SubmitInboundAsync(DeviceTask task, CancellationToken cancellationToken = default)
        {
            SubmissionCount++;
            return Task.FromResult(new DeviceOperationResult(task.IdempotencyKey, status, $"device-{task.WmsTaskId}"));
        }

        public Task<DeviceOperationResult> SubmitOutboundAsync(DeviceTask task, CancellationToken cancellationToken = default) => SubmitInboundAsync(task, cancellationToken);
        public Task<DeviceOperationResult> SubmitTransferAsync(DeviceTask task, CancellationToken cancellationToken = default) => SubmitInboundAsync(task, cancellationToken);

        public Task<DeviceResultObservation?> GetStatusAsync(string deviceTaskNumber, CancellationToken cancellationToken = default) =>
            Task.FromResult<DeviceResultObservation?>(capabilities.HasFlag(DeviceCapability.TaskQuery)
                ? new DeviceResultObservation(deviceTaskNumber, 1, status, DeviceObservationSource.Polling, DateTimeOffset.UtcNow)
                : null);

        public Task<DeviceOperationResult> TestConnectionAsync(string deviceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DeviceOperationResult($"connection:{deviceId}", DeviceOperationStatus.Succeeded));

        public Task<DeviceOperationResult> RequestStopAsync(DeviceTask task, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DeviceOperationResult(task.IdempotencyKey, DeviceOperationStatus.StopConfirmed));
    }
}
