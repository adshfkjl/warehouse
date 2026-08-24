namespace Warehouse.Wms.Domain.Tasks;

public enum TaskState
{
    Created,
    Allocated,
    Queued,
    Dispatching,
    SentToPlc,
    Executing,
    Succeeded,
    Failed,
    TimedOut,
    Canceled,
    CancelRequested,
    StopRequested,
    StopConfirmed,
    StopFailed,
    PhysicalStateUnknown,
    ManualIntervention
}

internal static class TaskStateTransitions
{
    private static readonly Dictionary<TaskState, HashSet<TaskState>> Allowed =
        new()
        {
            [TaskState.Created] = States(TaskState.Allocated, TaskState.Canceled),
            [TaskState.Allocated] = States(TaskState.Queued, TaskState.Canceled),
            [TaskState.Queued] = States(TaskState.Dispatching, TaskState.Failed, TaskState.Canceled),
            [TaskState.Dispatching] = States(TaskState.SentToPlc, TaskState.Failed, TaskState.TimedOut, TaskState.Canceled),
            [TaskState.SentToPlc] = States(
                TaskState.Executing, TaskState.Failed, TaskState.TimedOut,
                TaskState.CancelRequested, TaskState.StopRequested, TaskState.PhysicalStateUnknown),
            [TaskState.Executing] = States(
                TaskState.Succeeded, TaskState.Failed, TaskState.TimedOut,
                TaskState.CancelRequested, TaskState.StopRequested, TaskState.PhysicalStateUnknown),
            [TaskState.Failed] = States(TaskState.PhysicalStateUnknown, TaskState.ManualIntervention),
            [TaskState.TimedOut] = States(TaskState.PhysicalStateUnknown, TaskState.Failed, TaskState.ManualIntervention),
            [TaskState.CancelRequested] = States(TaskState.StopRequested),
            [TaskState.StopRequested] = States(TaskState.StopConfirmed, TaskState.StopFailed, TaskState.PhysicalStateUnknown),
            [TaskState.StopConfirmed] = States(TaskState.Canceled, TaskState.ManualIntervention),
            [TaskState.StopFailed] = States(TaskState.StopRequested, TaskState.PhysicalStateUnknown, TaskState.ManualIntervention),
            [TaskState.PhysicalStateUnknown] = States(
                TaskState.Executing, TaskState.Succeeded, TaskState.Failed, TaskState.ManualIntervention),
            [TaskState.Succeeded] = States(),
            [TaskState.Canceled] = States(),
            [TaskState.ManualIntervention] = States()
        };

    public static bool CanTransition(TaskState from, TaskState to)
        => Allowed.TryGetValue(from, out var states) && states.Contains(to);

    public static bool IsTerminal(TaskState state)
        => state is TaskState.Succeeded or TaskState.Canceled or TaskState.ManualIntervention;

    private static HashSet<TaskState> States(params TaskState[] states)
        => new HashSet<TaskState>(states);
}
