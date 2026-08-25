using Warehouse.Wms.Application.Authorization;
using Warehouse.Wms.Application.Devices;
using Warehouse.Wms.Application.Tasks;
using Warehouse.Wms.Domain.Devices;
using Warehouse.Wms.Domain.Tasks;

namespace Warehouse.Wms.UnitTests.Tasks;

public sealed class TaskCancellationTests
{
    [Fact]
    public async Task Queued_task_is_canceled_and_releases_scheduling_resources_without_device_stop()
    {
        var gateway = new FakeGateway(DeviceOperationStatus.StopConfirmed);
        var resources = new ResourceReleaseSpy();
        var service = new TaskCancellationService(gateway, resources);
        var request = NewRequest("queued", TaskState.Queued);

        var result = await service.CancelAsync(request, "operator-01", "operator canceled before dispatch");

        Assert.Equal(TaskState.Canceled, result.State);
        Assert.True(result.ResourcesReleased);
        Assert.Empty(gateway.StopRequests);
        Assert.Single(resources.Released);
    }

    [Fact]
    public async Task Dispatching_task_canceled_before_device_call_returns_to_queue_and_releases_scheduler_slot()
    {
        var gateway = new FakeGateway(DeviceOperationStatus.StopConfirmed);
        var resources = new ResourceReleaseSpy();
        var service = new TaskCancellationService(gateway, resources);
        var request = NewRequest("dispatching", TaskState.Dispatching);

        var result = await service.CancelAsync(
            request,
            "operator-01",
            "dispatch cancellation won the race",
            deviceCallStarted: false);

        Assert.Equal(TaskState.Queued, result.State);
        Assert.True(result.ResourcesReleased);
        Assert.Empty(gateway.StopRequests);
        Assert.Single(resources.Released);
    }

    [Fact]
    public async Task Sent_to_plc_task_requests_stop_and_cancels_only_after_stop_confirmation()
    {
        var gateway = new FakeGateway(DeviceOperationStatus.StopConfirmed);
        var resources = new ResourceReleaseSpy();
        var service = new TaskCancellationService(gateway, resources);
        var request = NewRequest("sent", TaskState.SentToPlc);

        var result = await service.CancelAsync(request, "operator-01", "cancel after device acceptance");

        Assert.Equal(TaskState.Canceled, result.State);
        Assert.True(result.StopRequested);
        Assert.True(result.ResourcesReleased);
        Assert.Single(gateway.StopRequests);
        Assert.Equal(
            new[] { TaskState.CancelRequested, TaskState.StopRequested, TaskState.StopConfirmed, TaskState.Canceled },
            request.Task.StateHistory.Select(history => history.ToState).TakeLast(4));
    }

    [Fact]
    public async Task Executing_task_with_stop_failure_keeps_resources_and_does_not_cancel()
    {
        var gateway = new FakeGateway(DeviceOperationStatus.StopFailed);
        var resources = new ResourceReleaseSpy();
        var service = new TaskCancellationService(gateway, resources);
        var request = NewRequest("stop-failed", TaskState.Executing);

        var result = await service.CancelAsync(request, "operator-01", "stop requested");

        Assert.Equal(TaskState.StopFailed, result.State);
        Assert.False(result.ResourcesReleased);
        Assert.Empty(resources.Released);
        Assert.Single(gateway.StopRequests);
    }

    [Fact]
    public async Task Stop_result_with_unknown_physical_state_keeps_resources()
    {
        var gateway = new FakeGateway(DeviceOperationStatus.PhysicalStateUnknown);
        var resources = new ResourceReleaseSpy();
        var service = new TaskCancellationService(gateway, resources);
        var request = NewRequest("stop-unknown", TaskState.Executing);

        var result = await service.CancelAsync(request, "operator-01", "stop response cannot be reconciled");

        Assert.Equal(TaskState.PhysicalStateUnknown, result.State);
        Assert.False(result.ResourcesReleased);
        Assert.Empty(resources.Released);
    }

    [Fact]
    public async Task Stop_response_with_wrong_idempotency_key_enters_physical_unknown()
    {
        var gateway = new FakeGateway(DeviceOperationStatus.StopConfirmed, "wrong-stop-key");
        var resources = new ResourceReleaseSpy();
        var service = new TaskCancellationService(gateway, resources);
        var request = NewRequest("stop-mismatch", TaskState.Executing);

        var result = await service.CancelAsync(request, "operator-01", "stop response does not match task");

        Assert.Equal(TaskState.PhysicalStateUnknown, result.State);
        Assert.False(result.ResourcesReleased);
        Assert.Empty(resources.Released);
    }

    [Fact]
    public async Task Physical_unknown_task_cannot_be_canceled_or_release_resources()
    {
        var gateway = new FakeGateway(DeviceOperationStatus.StopConfirmed);
        var resources = new ResourceReleaseSpy();
        var service = new TaskCancellationService(gateway, resources);
        var request = NewRequest("unknown", TaskState.PhysicalStateUnknown);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CancelAsync(request, "operator-01", "cannot prove device stopped"));

        Assert.Empty(gateway.StopRequests);
        Assert.Empty(resources.Released);
        Assert.Equal(TaskState.PhysicalStateUnknown, request.Task.State);
    }

    [Fact]
    public async Task Manual_physical_confirmation_rejects_missing_required_fields_before_authorization()
    {
        var authorization = new RecordingRiskAuthorizationService(true);
        var service = new PhysicalResultConfirmationService(new TestCurrentUser("operator-01"), authorization);
        var request = NewRequest("manual-missing", TaskState.PhysicalStateUnknown);

        await Assert.ThrowsAsync<ArgumentException>(() => service.ConfirmAsync(
            request.Task,
            new PhysicalResultConfirmationRequest(null, null, null, null, null, null)));

        Assert.Equal(TaskState.PhysicalStateUnknown, request.Task.State);
        Assert.Empty(authorization.Calls);
    }

    [Fact]
    public async Task Manual_physical_confirmation_requires_second_authorization_and_records_correction()
    {
        var authorization = new RecordingRiskAuthorizationService(true);
        var service = new PhysicalResultConfirmationService(new TestCurrentUser("operator-01"), authorization);
        var request = NewRequest("manual-confirm", TaskState.PhysicalStateUnknown);

        var confirmation = await service.ConfirmAsync(request.Task, ConfirmationRequest());

        Assert.Equal(TaskState.ManualIntervention, request.Task.State);
        Assert.NotEqual(TaskState.Succeeded, confirmation.State);
        Assert.Equal("operator-01", confirmation.ConfirmedBy);
        Assert.Equal("PALLET-01", confirmation.PalletNumber);
        Assert.Equal("JOURNAL-01", confirmation.InventoryCorrection.JournalNumber);
        Assert.Equal("Task.ManualPhysicalResultConfirmation", authorization.Calls.Single().Operation);
        Assert.Equal("人工确认物理结果并结案", request.Task.StateHistory.Single(history => history.ToState == TaskState.ManualIntervention).Reason);
    }

    [Fact]
    public async Task Manual_physical_confirmation_rejects_duplicate_confirmation()
    {
        var authorization = new RecordingRiskAuthorizationService(true);
        var service = new PhysicalResultConfirmationService(new TestCurrentUser("operator-01"), authorization);
        var request = NewRequest("manual-duplicate", TaskState.PhysicalStateUnknown);

        await service.ConfirmAsync(request.Task, ConfirmationRequest());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ConfirmAsync(request.Task, ConfirmationRequest()));
        Assert.Single(authorization.Calls);
    }

    private static PhysicalResultConfirmationRequest ConfirmationRequest()
        => new(
            "现场核对后校正",
            "Stopped and pallet verified",
            "A-01",
            "A-01",
            "B-01",
            new InventoryCorrectionJournal(
                "JOURNAL-01",
                "ITEM-01",
                1,
                "EA",
                "A-01",
                "B-01",
                "physical result reconciliation"),
            "PALLET-01");

    private static TaskDispatchRequest NewRequest(string number, TaskState state)
    {
        var task = new WarehouseTask(number, "Transfer");
        foreach (var nextState in StatesTo(state))
        {
            task.TransitionTo(nextState, "test", nextState.ToString());
        }

        return new TaskDispatchRequest(
            task,
            new DeviceTask($"idem-{number}", number, "PLC-01", "A-01", "B-01", "LP-01", "v1"),
            DeviceOperationKind.Transfer);
    }

    private static TaskState[] StatesTo(TaskState state)
    {
        var path = new[]
        {
            TaskState.Allocated, TaskState.Queued, TaskState.Dispatching,
            TaskState.SentToPlc, TaskState.Executing
        };

        if (state == TaskState.Created)
        {
            return [];
        }

        if (state == TaskState.PhysicalStateUnknown)
        {
            return [.. path, TaskState.TimedOut, TaskState.PhysicalStateUnknown];
        }

        var index = Array.IndexOf(path, state);
        return index >= 0 ? path[..(index + 1)] : throw new ArgumentOutOfRangeException(nameof(state));
    }

    private sealed class ResourceReleaseSpy : ITaskResourceReleaseCoordinator
    {
        public List<string> Released { get; } = [];

        public void ReleaseSchedulingResources(TaskDispatchRequest request)
            => Released.Add(request.Task.TaskNumber);
    }

    private sealed record TestCurrentUser(string UserId) : ICurrentUser;

    private sealed class RecordingRiskAuthorizationService(bool result) : IRiskAuthorizationService
    {
        public List<AuthorizationCall> Calls { get; } = [];

        public Task<bool> AuthorizeAsync(
            string operation,
            ICurrentUser user,
            string taskNumber,
            string reason,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(new AuthorizationCall(operation, user.UserId, taskNumber, reason));
            return Task.FromResult(result);
        }
    }

    private sealed record AuthorizationCall(string Operation, string UserId, string TaskNumber, string Reason);

    private sealed class FakeGateway(
        DeviceOperationStatus stopStatus,
        string? responseIdempotencyKey = null) : IWarehouseDeviceGateway
    {
        public List<DeviceTask> StopRequests { get; } = [];

        public Task<DeviceOperationResult> SubmitInboundAsync(DeviceTask task, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<DeviceOperationResult> SubmitOutboundAsync(DeviceTask task, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<DeviceOperationResult> SubmitTransferAsync(DeviceTask task, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<DeviceResultObservation?> GetStatusAsync(string deviceTaskNumber, CancellationToken cancellationToken = default)
            => Task.FromResult<DeviceResultObservation?>(null);

        public Task<DeviceOperationResult> TestConnectionAsync(string deviceId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<DeviceOperationResult> RequestStopAsync(DeviceTask task, CancellationToken cancellationToken = default)
        {
            StopRequests.Add(task);
            return Task.FromResult(new DeviceOperationResult(responseIdempotencyKey ?? task.IdempotencyKey, stopStatus));
        }
    }
}
