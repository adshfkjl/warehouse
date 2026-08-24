namespace Warehouse.Wms.Domain.Tasks;

public sealed class TaskStateHistory
{
    private TaskStateHistory() { }

    internal TaskStateHistory(
        Guid taskId,
        TaskState fromState,
        TaskState toState,
        string @operator,
        string reason,
        string? errorCode,
        int version,
        DateTimeOffset occurredAt)
    {
        Id = Guid.NewGuid();
        TaskId = taskId;
        FromState = fromState;
        ToState = toState;
        Operator = Require(@operator, nameof(@operator));
        Reason = Require(reason, nameof(reason));
        ErrorCode = errorCode?.Trim();
        Version = version;
        OccurredAt = occurredAt.ToUniversalTime();
    }

    public Guid Id { get; private set; }
    public Guid TaskId { get; private set; }
    public TaskState FromState { get; private set; }
    public TaskState ToState { get; private set; }
    public string Operator { get; private set; } = null!;
    public string Reason { get; private set; } = null!;
    public string? ErrorCode { get; private set; }
    public int Version { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }

    private static string Require(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty value is required.", parameterName);
        }

        return value.Trim();
    }
}
