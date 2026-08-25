namespace Warehouse.Wms.Domain.Exceptions;

public sealed record ExceptionResourceSnapshot(
    IReadOnlyList<string>? PalletIds = null,
    IReadOnlyList<string>? LocationIds = null,
    IReadOnlyList<string>? LoadingPointIds = null,
    IReadOnlyList<string>? InventoryBalanceIds = null,
    IReadOnlyList<string>? ResourceLockIds = null)
{
    public IReadOnlyList<string> PalletIds { get; } = Normalize(PalletIds);
    public IReadOnlyList<string> LocationIds { get; } = Normalize(LocationIds);
    public IReadOnlyList<string> LoadingPointIds { get; } = Normalize(LoadingPointIds);
    public IReadOnlyList<string> InventoryBalanceIds { get; } = Normalize(InventoryBalanceIds);
    public IReadOnlyList<string> ResourceLockIds { get; } = Normalize(ResourceLockIds);

    private static string[] Normalize(IReadOnlyList<string>? values)
        => values is null
            ? Array.Empty<string>()
            : values.Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();
}

public sealed class ExceptionWorkItem
{
    private readonly List<ExceptionAuditEntry> _auditTrail = [];

    public ExceptionWorkItem(
        string source,
        ExceptionType type,
        ExceptionSeverity severity,
        string externalKey,
        Guid taskId,
        string taskNumber,
        string? deviceTaskNumber,
        ExceptionPhysicalState physicalState,
        ExceptionResourceSnapshot resources,
        string? deviceObservation,
        DateTimeOffset? occurredAt = null)
    {
        Id = Guid.NewGuid();
        Source = Require(source, nameof(source));
        Type = type;
        Severity = severity;
        ExternalKey = Require(externalKey, nameof(externalKey));
        if (taskId == Guid.Empty)
        {
            throw new ArgumentException("A task id is required.", nameof(taskId));
        }

        TaskId = taskId;
        TaskNumber = Require(taskNumber, nameof(taskNumber));
        DeviceTaskNumber = deviceTaskNumber?.Trim();
        PhysicalState = physicalState;
        Resources = resources ?? throw new ArgumentNullException(nameof(resources));
        DeviceObservation = deviceObservation?.Trim();
        CreatedAt = Normalize(occurredAt ?? DateTimeOffset.UtcNow);
        UpdatedAt = CreatedAt;
        Status = ExceptionWorkItemStatus.Active;
    }

    public Guid Id { get; }
    public string Source { get; }
    public ExceptionType Type { get; private set; }
    public ExceptionSeverity Severity { get; private set; }
    public string ExternalKey { get; }
    public Guid TaskId { get; }
    public string TaskNumber { get; }
    public string? DeviceTaskNumber { get; private set; }
    public ExceptionPhysicalState PhysicalState { get; private set; }
    public ExceptionResourceSnapshot Resources { get; private set; }
    public string? DeviceObservation { get; private set; }
    public ExceptionWorkItemStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public IReadOnlyList<ExceptionAuditEntry> AuditTrail => _auditTrail.AsReadOnly();

    public string IdentityKey => $"{Source}|{ExternalKey}|{TaskId:D}";

    public void Merge(
        ExceptionType type,
        ExceptionSeverity severity,
        string? deviceTaskNumber,
        ExceptionPhysicalState physicalState,
        ExceptionResourceSnapshot resources,
        string? deviceObservation,
        string @operator,
        string reason,
        DateTimeOffset? occurredAt = null)
    {
        Type = type;
        Severity = severity;
        DeviceTaskNumber = deviceTaskNumber?.Trim() ?? DeviceTaskNumber;
        PhysicalState = physicalState;
        Resources = resources ?? throw new ArgumentNullException(nameof(resources));
        DeviceObservation = deviceObservation?.Trim() ?? DeviceObservation;
        Touch(occurredAt);
        AddAudit(ExceptionAction.Merged, @operator, reason, Status, Status, DeviceObservation, occurredAt);
    }

    public void Reopen(
        ExceptionType type,
        ExceptionSeverity severity,
        string? deviceTaskNumber,
        ExceptionPhysicalState physicalState,
        ExceptionResourceSnapshot resources,
        string? deviceObservation,
        string @operator,
        string reason,
        DateTimeOffset? occurredAt = null)
    {
        var before = Status;
        Type = type;
        Severity = severity;
        DeviceTaskNumber = deviceTaskNumber?.Trim() ?? DeviceTaskNumber;
        PhysicalState = physicalState;
        Resources = resources ?? throw new ArgumentNullException(nameof(resources));
        DeviceObservation = deviceObservation?.Trim() ?? DeviceObservation;
        Status = ExceptionWorkItemStatus.Active;
        Touch(occurredAt);
        AddAudit(ExceptionAction.Reopened, @operator, reason, before, Status, DeviceObservation, occurredAt);
    }

    public void ApplyOutcome(
        ExceptionAction action,
        ExceptionActionOutcome outcome,
        string @operator,
        string reason,
        DateTimeOffset? occurredAt = null)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var before = Status;
        if ((PhysicalState == ExceptionPhysicalState.Unknown
                && outcome.Status == ExceptionWorkItemStatus.Closed
                && outcome.PhysicalState == ExceptionPhysicalState.Unknown)
            || (outcome.PhysicalState == ExceptionPhysicalState.Unknown
                && (outcome.Status == ExceptionWorkItemStatus.Resolved
                    || outcome.ResourcesReleased)))
        {
            throw new InvalidOperationException(
                "An exception with unknown physical state cannot be resolved, closed, or release resources.");
        }

        PhysicalState = outcome.PhysicalState;
        Status = outcome.Status;
        Touch(occurredAt);
        AddAudit(action, @operator, reason, before, Status, outcome.Message, occurredAt);
    }

    public void AddRejectedAudit(ExceptionAction action, string @operator, string reason, string? observation = null)
    {
        Touch(null);
        AddAudit(ExceptionAction.ActionRejected, @operator, reason, Status, Status, observation ?? action.ToString(), null);
    }

    public void AddAlertedAudit(string @operator, string reason, string? observation = null)
    {
        Touch(null);
        AddAudit(ExceptionAction.Alerted, @operator, reason, Status, Status, observation, null);
    }

    private void AddAudit(
        ExceptionAction action,
        string @operator,
        string reason,
        ExceptionWorkItemStatus before,
        ExceptionWorkItemStatus after,
        string? observation,
        DateTimeOffset? occurredAt)
    {
        _auditTrail.Add(new ExceptionAuditEntry(
            Guid.NewGuid(),
            Id,
            action,
            Require(@operator, nameof(@operator)),
            Require(reason, nameof(reason)),
            before,
            after,
            observation,
            Normalize(occurredAt ?? UpdatedAt)));
    }

    private void Touch(DateTimeOffset? timestamp)
        => UpdatedAt = Normalize(timestamp ?? DateTimeOffset.UtcNow);

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

public sealed record ExceptionAuditEntry(
    Guid Id,
    Guid ExceptionWorkItemId,
    ExceptionAction Action,
    string Operator,
    string Reason,
    ExceptionWorkItemStatus BeforeStatus,
    ExceptionWorkItemStatus AfterStatus,
    string? DeviceObservation,
    DateTimeOffset OccurredAt);

public sealed record ExceptionActionOutcome(
    ExceptionWorkItemStatus Status,
    ExceptionPhysicalState PhysicalState,
    bool ResourcesReleased,
    string? Message);
