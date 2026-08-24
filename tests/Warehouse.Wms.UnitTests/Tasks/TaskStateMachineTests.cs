using Warehouse.Wms.Domain.Tasks;

namespace Warehouse.Wms.UnitTests.Tasks;

public sealed class TaskStateMachineTests
{
    [Fact]
    public void Task_state_dictionary_contains_every_frozen_state()
    {
        var expected = new[]
        {
            "Created", "Allocated", "Queued", "Dispatching", "SentToPlc", "Executing",
            "Succeeded", "Failed", "TimedOut", "Canceled", "CancelRequested", "StopRequested",
            "StopConfirmed", "StopFailed", "PhysicalStateUnknown", "ManualIntervention"
        };

        Assert.Equal(expected, Enum.GetNames<TaskState>());
    }

    [Fact]
    public void Task_can_complete_through_dispatch_and_execution_states()
    {
        var task = NewTask();

        Transition(task, TaskState.Allocated, "allocator", "resources allocated");
        Transition(task, TaskState.Queued, "scheduler", "ready for dispatch");
        Transition(task, TaskState.Dispatching, "scheduler", "dispatch attempt started");
        Transition(task, TaskState.SentToPlc, "gateway", "accepted by PLC");
        Transition(task, TaskState.Executing, "gateway", "execution observed");
        Transition(task, TaskState.Succeeded, "gateway", "device completed");

        Assert.Equal(TaskState.Succeeded, task.State);
        Assert.Equal(6, task.StateHistory.Count);
    }

    [Fact]
    public void History_records_operator_reason_error_and_utc_time_for_each_transition()
    {
        var task = NewTask();
        var occurredAt = new DateTimeOffset(2026, 8, 25, 10, 30, 0, TimeSpan.FromHours(8));

        task.TransitionTo(TaskState.Allocated, "user-01", "allocate", "ALLOC-001", occurredAt);

        var history = Assert.Single(task.StateHistory);
        Assert.Equal(TaskState.Created, history.FromState);
        Assert.Equal(TaskState.Allocated, history.ToState);
        Assert.Equal("user-01", history.Operator);
        Assert.Equal("allocate", history.Reason);
        Assert.Equal("ALLOC-001", history.ErrorCode);
        Assert.Equal(2, history.Version);
        Assert.Equal(2, task.Version);
        Assert.Equal(TimeSpan.Zero, history.OccurredAt.Offset);
        Assert.Equal(occurredAt.ToUniversalTime(), history.OccurredAt);
        Assert.Equal(task.Id, history.TaskId);
    }

    [Fact]
    public void Unreleased_task_can_be_canceled_but_sent_or_executing_task_requires_stop_protocol()
    {
        var queued = NewTask();
        Transition(queued, TaskState.Allocated, "system", "allocated");
        Transition(queued, TaskState.Queued, "system", "queued");
        Transition(queued, TaskState.Canceled, "operator", "canceled before dispatch");

        Assert.Throws<InvalidOperationException>(() => queued.TransitionTo(
            TaskState.Succeeded, "operator", "must not complete canceled task"));

        var executing = NewTask();
        foreach (var state in new[] { TaskState.Allocated, TaskState.Queued, TaskState.Dispatching, TaskState.SentToPlc, TaskState.Executing })
        {
            Transition(executing, state, "system", state.ToString());
        }

        Assert.Throws<InvalidOperationException>(() => executing.TransitionTo(
            TaskState.Canceled, "operator", "physical action may still be running"));

        Transition(executing, TaskState.CancelRequested, "operator", "cancel requested");
        Transition(executing, TaskState.StopRequested, "gateway", "stop requested");
        Transition(executing, TaskState.StopConfirmed, "gateway", "stop confirmed");
    }

    [Fact]
    public void Timeout_and_unknown_result_require_explicit_reconciliation()
    {
        var task = NewTask();
        foreach (var state in new[] { TaskState.Allocated, TaskState.Queued, TaskState.Dispatching, TaskState.SentToPlc, TaskState.Executing })
        {
            Transition(task, state, "system", state.ToString());
        }

        Transition(task, TaskState.TimedOut, "watchdog", "software timeout", "TASK_TIMEOUT");
        Transition(task, TaskState.PhysicalStateUnknown, "watchdog", "task result cannot be reconciled", "PHYSICAL_UNKNOWN");

        Assert.Equal(TaskState.PhysicalStateUnknown, task.State);
        Assert.Throws<InvalidOperationException>(() => task.TransitionTo(
            TaskState.Canceled, "system", "unknown physical result cannot be canceled"));

        Transition(task, TaskState.Failed, "operator", "reconciliation failed", "RECONCILE_FAILED");
        Assert.Equal(TaskState.Failed, task.State);
    }

    [Fact]
    public void Terminal_states_reject_duplicate_completion_or_further_transition()
    {
        var task = NewTask();
        Transition(task, TaskState.Canceled, "operator", "cancel before dispatch");

        Assert.Throws<InvalidOperationException>(() => task.TransitionTo(
            TaskState.Canceled, "operator", "duplicate cancellation"));
        Assert.Throws<InvalidOperationException>(() => task.TransitionTo(
            TaskState.Allocated, "operator", "terminal task cannot be reopened"));

        var manual = NewTask();
        foreach (var state in new[] { TaskState.Allocated, TaskState.Queued, TaskState.Dispatching, TaskState.SentToPlc, TaskState.Executing, TaskState.PhysicalStateUnknown })
        {
            Transition(manual, state, "system", state.ToString());
        }

        Transition(manual, TaskState.ManualIntervention, "operator", "manual physical confirmation required");
        Assert.Throws<InvalidOperationException>(() => manual.TransitionTo(
            TaskState.Succeeded, "operator", "manual intervention is not an automatic success"));
    }

    [Fact]
    public void Invalid_transition_and_missing_history_context_are_rejected()
    {
        var task = NewTask();

        Assert.Throws<InvalidOperationException>(() => task.TransitionTo(
            TaskState.Executing, "system", "cannot skip allocation"));
        Assert.Throws<ArgumentException>(() => task.TransitionTo(
            TaskState.Allocated, " ", "reason"));
        Assert.Throws<ArgumentException>(() => task.TransitionTo(
            TaskState.Allocated, "system", " "));
    }

    private static WarehouseTask NewTask() => new("TASK-001", "Putaway");

    private static void Transition(WarehouseTask task, TaskState state, string @operator, string reason, string? errorCode = null)
        => task.TransitionTo(state, @operator, reason, errorCode);
}
