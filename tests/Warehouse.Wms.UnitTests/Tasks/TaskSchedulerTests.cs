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

    [Fact]
    public async Task Scheduler_claims_device_command_before_gateway_and_publishes_after_acceptance()
    {
        var outbox = new RecordingTaskCommandOutbox();
        var gateway = new FakeGateway(DeviceOperationStatus.Accepted, events: outbox.Events);
        var scheduler = new WmsTaskScheduler(gateway, commandOutbox: outbox, workerId: "scheduler-test");
        var request = Request("outbox-accepted", "PLC-01");

        await scheduler.EnqueueAsync(request);
        var result = await scheduler.DispatchNextAsync();

        Assert.NotNull(result);
        Assert.Equal(DeviceOperationStatus.Accepted, result!.Status);
        Assert.Equal(["claim", "gateway", "published"], outbox.Events);
        Assert.Equal(request.DeviceTask.IdempotencyKey, outbox.ClaimedKey);
        Assert.Single(gateway.SubmittedTaskNumbers);
    }

    [Fact]
    public async Task Scheduler_persists_enqueue_and_dispatch_state_transitions()
    {
        var persistence = new InMemoryTaskPersistenceStore();
        var gateway = new FakeGateway(DeviceOperationStatus.Accepted);
        var scheduler = new WmsTaskScheduler(gateway, persistenceStore: persistence);
        var request = Request("persisted", "PLC-01");

        await scheduler.EnqueueAsync(request);
        await scheduler.DispatchNextAsync();

        var restored = await persistence.GetTaskAsync("persisted");
        Assert.NotNull(restored);
        Assert.Equal(TaskState.SentToPlc, restored!.State);
        Assert.Equal(5, restored.Version);
        Assert.Equal(4, restored.StateHistory.Count);
    }

    [Fact]
    public async Task Scheduler_recovery_does_not_resubmit_in_flight_task()
    {
        var persistence = new InMemoryTaskPersistenceStore();
        var gateway = new RecoveryGateway(DeviceCapability.TaskQuery, DeviceOperationStatus.Executing);
        var state = new TaskSchedulerState();
        var scheduler = new WmsTaskScheduler(gateway, state: state, persistenceStore: persistence);
        var request = Request("recover-persisted", "PLC-01");

        await scheduler.EnqueueAsync(request);
        await scheduler.DispatchNextAsync();
        await scheduler.RecoverInFlightAsync();

        Assert.Equal(1, gateway.SubmissionCount);
        Assert.Equal(TaskState.Executing, request.Task.State);
    }

    [Fact]
    public async Task Scheduler_marks_durable_in_flight_task_unknown_on_restart_without_replay()
    {
        var persistence = new InMemoryTaskPersistenceStore();
        var task = new WarehouseTask("durable-restart", "Putaway");
        await persistence.CreateTaskAsync(task);
        await persistence.TransitionTaskAsync(task.TaskNumber, task.Version, TaskState.Allocated, "scheduler", "allocated");
        await persistence.TransitionTaskAsync(task.TaskNumber, task.Version, TaskState.Queued, "scheduler", "queued");
        await persistence.TransitionTaskAsync(task.TaskNumber, task.Version, TaskState.Dispatching, "scheduler", "dispatching");

        var gateway = new FakeGateway(DeviceOperationStatus.Accepted);
        var scheduler = new WmsTaskScheduler(gateway, persistenceStore: persistence);
        await scheduler.RecoverPersistedInFlightAsync();

        var restored = await persistence.GetTaskAsync(task.TaskNumber);
        Assert.Equal(TaskState.PhysicalStateUnknown, restored!.State);
        Assert.Empty(gateway.SubmittedTaskNumbers);
    }

    [Fact]
    public async Task Scheduler_persists_dispatch_context_for_restart_recovery()
    {
        var persistence = new InMemoryTaskPersistenceStore();
        var scheduler = new WmsTaskScheduler(new FakeGateway(DeviceOperationStatus.Accepted), persistenceStore: persistence);
        var request = Request("context-restart", "PLC-01");
        await scheduler.EnqueueAsync(request);
        var restored = await persistence.GetTaskAsync(request.Task.TaskNumber);
        Assert.NotNull(restored?.DispatchContextJson);
        Assert.Contains("PLC-01", restored!.DispatchContextJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scheduler_rebuilds_queued_dispatch_context_after_restart()
    {
        var persistence = new InMemoryTaskPersistenceStore();
        var first = new WmsTaskScheduler(new FakeGateway(DeviceOperationStatus.Accepted), persistenceStore: persistence);
        var request = Request("queued-restart", "PLC-07", priority: 9);
        request = new TaskDispatchRequest(request.Task, request.DeviceTask, request.OperationKind, priority: 9, maxAttempts: 4);

        await first.EnqueueAsync(request);

        var gateway = new FakeGateway(DeviceOperationStatus.Accepted);
        var restarted = new WmsTaskScheduler(gateway, persistenceStore: persistence);
        await restarted.RecoverPersistedInFlightAsync();
        var result = await restarted.DispatchNextAsync();

        Assert.NotNull(result);
        Assert.Equal("queued-restart", result!.Request.Task.TaskNumber);
        Assert.Equal(9, result.Request.Priority);
        Assert.Equal(4, result.Request.MaxAttempts);
        Assert.Single(gateway.SubmittedTaskNumbers);
    }

    [Fact]
    public async Task Scheduler_reconciles_persisted_in_flight_device_task_after_restart()
    {
        var persistence = new InMemoryTaskPersistenceStore();
        var firstGateway = new FakeGateway(DeviceOperationStatus.Accepted);
        var first = new WmsTaskScheduler(firstGateway, persistenceStore: persistence);
        var request = Request("inflight-restart", "PLC-08");
        await first.EnqueueAsync(request);
        await first.DispatchNextAsync();

        var secondGateway = new RecoveryGateway(DeviceCapability.TaskQuery, DeviceOperationStatus.Executing);
        var restarted = new WmsTaskScheduler(secondGateway, persistenceStore: persistence);
        await restarted.RecoverPersistedInFlightAsync();
        await restarted.RecoverInFlightAsync();

        var restored = await persistence.GetTaskAsync("inflight-restart");
        Assert.Equal(TaskState.Executing, restored!.State);
        Assert.Equal(0, secondGateway.SubmissionCount);
    }

    [Fact]
    public async Task Restart_recovery_claims_device_for_persisted_in_flight_task()
    {
        var persistence = new InMemoryTaskPersistenceStore();
        var first = new WmsTaskScheduler(new FakeGateway(DeviceOperationStatus.Accepted), persistenceStore: persistence);
        await first.EnqueueAsync(Request("active-device", "PLC-A"));
        await first.DispatchNextAsync();

        var gateway = new RecoveryGateway(DeviceCapability.TaskQuery, DeviceOperationStatus.Executing);
        var restarted = new WmsTaskScheduler(gateway, persistenceStore: persistence);
        await restarted.RecoverPersistedInFlightAsync();
        var queued = Request("same-device-queued", "PLC-A");
        await restarted.EnqueueAsync(queued);

        Assert.Null(await restarted.DispatchNextAsync());
    }

    [Fact]
    public async Task Persistent_device_lease_serializes_two_scheduler_processes()
    {
        var store = new InMemoryTaskPersistenceStore();
        var firstGateway = new FakeGateway(DeviceOperationStatus.Accepted);
        var secondGateway = new FakeGateway(DeviceOperationStatus.Accepted);
        var first = new WmsTaskScheduler(firstGateway, persistenceStore: store);
        var second = new WmsTaskScheduler(secondGateway, persistenceStore: store);
        await first.EnqueueAsync(Request("lease-1", "PLC-LEASE"));
        await second.EnqueueAsync(Request("lease-2", "PLC-LEASE"));

        var firstResult = await first.DispatchNextAsync();
        var secondResult = await second.DispatchNextAsync();

        Assert.NotNull(firstResult);
        Assert.Null(secondResult);
        Assert.Single(firstGateway.SubmittedTaskNumbers);
        Assert.Empty(secondGateway.SubmittedTaskNumbers);
    }

    [Fact]
    public async Task Workflow_context_is_persisted_and_recovery_can_be_marked_blocked()
    {
        var store = new InMemoryTaskPersistenceStore();
        var task = new WarehouseTask("workflow-context", "Outbound");
        await store.CreateTaskAsync(task);
        await store.UpdateWorkflowContextAsync(task.TaskNumber, "OutboundReview", "OB-001", "{\"order\":\"OB-001\"}");
        await store.UpdateWorkflowContextAsync(task.TaskNumber, "OutboundReview", "OB-001", "{\"order\":\"OB-001\"}", WorkflowRecoveryStatus.BlockedMissingBusinessState);

        var restored = await store.GetTaskAsync(task.TaskNumber);
        Assert.Equal("OutboundReview", restored!.WorkflowKind);
        Assert.Equal("OB-001", restored.WorkflowReference);
        Assert.Equal(WorkflowRecoveryStatus.BlockedMissingBusinessState, restored.WorkflowRecoveryStatus);
    }

    [Fact]
    public async Task Workflow_recovery_reports_blocked_instead_of_completing_missing_business_state()
    {
        var store = new InMemoryTaskPersistenceStore();
        var task = new WarehouseTask("recover-outbound", "Outbound");
        task.SetWorkflowContext("OutboundReview", "OB-001", "{bad-json");
        await store.CreateTaskAsync(task);
        var recovery = new WorkflowRecoveryService(store);

        var result = await recovery.RecoverAsync("OutboundReview");

        Assert.Single(result);
        Assert.Equal(WorkflowRecoveryStatus.BlockedMissingBusinessState, result[0].Status);
        Assert.Equal(WorkflowRecoveryStatus.BlockedMissingBusinessState,
            (await store.GetTaskAsync(task.TaskNumber))!.WorkflowRecoveryStatus);
    }

    [Fact]
    public async Task Workflow_recovery_keeps_scheduler_snapshot_recoverable()
    {
        var store = new InMemoryTaskPersistenceStore();
        var task = new WarehouseTask("recoverable-outbound", "Outbound");
        task.SetWorkflowContext("Outbound", task.TaskNumber, "{\"order\":\"OB-002\"}");
        task.MarkWorkflowRecovered();
        await store.CreateTaskAsync(task);
        var recovery = new WorkflowRecoveryService(store);

        var result = await recovery.RecoverAsync("Outbound");

        Assert.Single(result);
        Assert.Equal(WorkflowRecoveryStatus.RecoveredFromSnapshot, result[0].Status);
        Assert.Equal(WorkflowRecoveryStatus.RecoveredFromSnapshot,
            (await store.GetTaskAsync(task.TaskNumber))!.WorkflowRecoveryStatus);
    }

    [Fact]
    public async Task Workflow_recovery_marks_valid_snapshot_recovered()
    {
        var store = new InMemoryTaskPersistenceStore();
        var task = new WarehouseTask("recover-valid", "Outbound");
        task.SetWorkflowContext("OutboundReview", "OB-002", "{\"order\":\"OB-002\"}");
        await store.CreateTaskAsync(task);
        var result = await new WorkflowRecoveryService(store).RecoverAsync("OutboundReview");
        Assert.Equal(WorkflowRecoveryStatus.RecoveredFromSnapshot, result.Single().Status);
        Assert.Equal(WorkflowRecoveryStatus.RecoveredFromSnapshot, (await store.GetTaskAsync(task.TaskNumber))!.WorkflowRecoveryStatus);
    }

    [Fact]
    public async Task Malformed_dispatch_context_is_blocked_and_not_dispatched()
    {
        var store = new InMemoryTaskPersistenceStore();
        var task = new WarehouseTask("bad-context", "Outbound");
        task.SetDispatchContext("{bad-json");
        await store.CreateTaskAsync(task);
        await store.TransitionTaskAsync(task.TaskNumber, task.Version, TaskState.Allocated, "test", "allocated");
        await store.TransitionTaskAsync(task.TaskNumber, task.Version, TaskState.Queued, "test", "queued");

        var scheduler = new WmsTaskScheduler(new FakeGateway(DeviceOperationStatus.Accepted), persistenceStore: store);
        await scheduler.RecoverPersistedInFlightAsync();

        var restored = await store.GetTaskAsync(task.TaskNumber);
        Assert.Equal(WorkflowRecoveryStatus.BlockedMissingBusinessState, restored!.WorkflowRecoveryStatus);
        Assert.Null(await scheduler.DispatchNextAsync());
    }

    [Fact]
    public async Task Scheduler_keeps_unknown_command_non_dispatchable_and_never_retries_it()
    {
        var outbox = new RecordingTaskCommandOutbox();
        var gateway = new FakeGateway(DeviceOperationStatus.PhysicalStateUnknown, events: outbox.Events);
        var scheduler = new WmsTaskScheduler(gateway, commandOutbox: outbox, workerId: "scheduler-test");
        var request = Request("outbox-unknown", "PLC-01");

        await scheduler.EnqueueAsync(request);
        var result = await scheduler.DispatchNextAsync();

        Assert.NotNull(result);
        Assert.Equal(DeviceOperationStatus.PhysicalStateUnknown, result!.Status);
        Assert.Equal(TaskState.PhysicalStateUnknown, request.Task.State);
        Assert.Equal(["claim", "gateway", "failed"], outbox.Events);
        Assert.Equal(DateTimeOffset.MaxValue, outbox.NextAttemptAt);
        Assert.Null(await scheduler.DispatchNextAsync());
        Assert.Single(gateway.SubmittedTaskNumbers);
    }

    private static TaskDispatchRequest Request(string number, string deviceId, int priority = 0) =>
        new(
            new WarehouseTask(number, "Putaway"),
            new DeviceTask($"idem-{number}", number, deviceId, "1-1", "2-1", "0", "v1"),
            DeviceOperationKind.Inbound,
            priority);

    private sealed class FakeGateway(
        DeviceOperationStatus submissionStatus,
        string? responseIdempotencyKey = null,
        List<string>? events = null) : IWarehouseDeviceGateway
    {
        public List<string> SubmittedTaskNumbers { get; } = [];

        public Task<DeviceOperationResult> SubmitInboundAsync(DeviceTask task, CancellationToken cancellationToken = default)
        {
            events?.Add("gateway");
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
        public Task<DeviceOperationResult> TestConnectionAsync(string deviceId, CancellationToken cancellationToken = default) => Task.FromResult(new DeviceOperationResult(deviceId, DeviceOperationStatus.Succeeded));
        public Task<DeviceOperationResult> RequestStopAsync(DeviceTask task, CancellationToken cancellationToken = default) => Task.FromResult(new DeviceOperationResult(task.IdempotencyKey, DeviceOperationStatus.StopConfirmed));
    }

    private sealed class RecordingTaskCommandOutbox : ITaskCommandOutbox
    {
        private readonly TaskCommandOutboxClaim _claim = new(Guid.NewGuid(), "outbox-command", "scheduler-test");

        public List<string> Events { get; } = [];
        public string? ClaimedKey { get; private set; }
        public DateTimeOffset NextAttemptAt { get; private set; }

        public Task<TaskCommandOutboxClaim?> EnqueueAndClaimAsync(
            DeviceTask task,
            DeviceOperationKind operationKind,
            string workerId,
            DateTimeOffset claimedAt,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
        {
            Events.Add("claim");
            ClaimedKey = task.IdempotencyKey;
            return Task.FromResult<TaskCommandOutboxClaim?>(_claim);
        }

        public Task MarkPublishedAsync(
            TaskCommandOutboxClaim claim,
            DateTimeOffset publishedAt,
            CancellationToken cancellationToken = default)
        {
            Events.Add("published");
            return Task.CompletedTask;
        }

        public Task MarkFailedAsync(
            TaskCommandOutboxClaim claim,
            string failure,
            DateTimeOffset failedAt,
            DateTimeOffset nextAttemptAt,
            CancellationToken cancellationToken = default)
        {
            Events.Add("failed");
            NextAttemptAt = nextAttemptAt;
            return Task.CompletedTask;
        }
    }
}
