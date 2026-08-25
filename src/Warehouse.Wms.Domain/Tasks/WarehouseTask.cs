namespace Warehouse.Wms.Domain.Tasks;

public enum WorkflowRecoveryStatus
{
    Pending,
    RecoveredFromSnapshot,
    BlockedMissingBusinessState
}

public sealed class WarehouseTask
{
    private readonly List<TaskStateHistory> _stateHistory = [];

    private WarehouseTask() { }

    public WarehouseTask(string taskNumber, string taskType, DateTimeOffset? createdAt = null, Guid? id = null)
    {
        Id = id.GetValueOrDefault(Guid.NewGuid());
        if (Id == Guid.Empty) throw new ArgumentException("A task id is required.", nameof(id));
        TaskNumber = Require(taskNumber, nameof(taskNumber));
        TaskType = Require(taskType, nameof(taskType));
        CreatedAt = Normalize(createdAt ?? DateTimeOffset.UtcNow);
        UpdatedAt = CreatedAt;
    }

    public Guid Id { get; private set; }
    public string TaskNumber { get; private set; } = null!;
    public string TaskType { get; private set; } = null!;
    public TaskState State { get; private set; } = TaskState.Created;
    public int Version { get; private set; } = 1;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public string? DispatchContextJson { get; private set; }
    public string? WorkflowKind { get; private set; }
    public string? WorkflowReference { get; private set; }
    public string? WorkflowSnapshotJson { get; private set; }
    public WorkflowRecoveryStatus WorkflowRecoveryStatus { get; private set; } = WorkflowRecoveryStatus.Pending;
    public IReadOnlyList<TaskStateHistory> StateHistory => _stateHistory.AsReadOnly();

    public void SetDispatchContext(string contextJson)
    {
        if (string.IsNullOrWhiteSpace(contextJson)) throw new ArgumentException("A dispatch context is required.", nameof(contextJson));
        DispatchContextJson = contextJson.Trim();
    }

    public void SetWorkflowContext(string workflowKind, string workflowReference, string snapshotJson)
    {
        if (string.IsNullOrWhiteSpace(workflowKind)) throw new ArgumentException("A workflow kind is required.", nameof(workflowKind));
        if (string.IsNullOrWhiteSpace(workflowReference)) throw new ArgumentException("A workflow reference is required.", nameof(workflowReference));
        if (string.IsNullOrWhiteSpace(snapshotJson)) throw new ArgumentException("A workflow snapshot is required.", nameof(snapshotJson));
        WorkflowKind = workflowKind.Trim();
        WorkflowReference = workflowReference.Trim();
        WorkflowSnapshotJson = snapshotJson.Trim();
        WorkflowRecoveryStatus = WorkflowRecoveryStatus.Pending;
    }

    public void MarkWorkflowRecoveryBlocked() => WorkflowRecoveryStatus = WorkflowRecoveryStatus.BlockedMissingBusinessState;
    public void MarkWorkflowRecovered() => WorkflowRecoveryStatus = WorkflowRecoveryStatus.RecoveredFromSnapshot;

    public void TransitionTo(
        TaskState nextState,
        string @operator,
        string reason,
        string? errorCode = null,
        DateTimeOffset? occurredAt = null)
    {
        if (TaskStateTransitions.IsTerminal(State))
        {
            throw new InvalidOperationException($"Task '{TaskNumber}' is terminal in state '{State}'.");
        }

        if (!TaskStateTransitions.CanTransition(State, nextState))
        {
            throw new InvalidOperationException($"Task state transition '{State}' -> '{nextState}' is not allowed.");
        }

        var timestamp = Normalize(occurredAt ?? DateTimeOffset.UtcNow);
        var nextVersion = checked(Version + 1);
        var history = new TaskStateHistory(Id, State, nextState, @operator, reason, errorCode, nextVersion, timestamp);
        _stateHistory.Add(history);
        State = nextState;
        Version = nextVersion;
        UpdatedAt = timestamp;
    }

    private static string Require(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty value is required.", parameterName);
        }

        return value.Trim();
    }

    private static DateTimeOffset Normalize(DateTimeOffset value) => value.ToUniversalTime();
}
