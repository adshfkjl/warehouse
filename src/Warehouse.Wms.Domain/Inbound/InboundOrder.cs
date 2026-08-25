namespace Warehouse.Wms.Domain.Inbound;

public sealed record InboundStateHistory(
    Guid Id,
    Guid InboundOrderId,
    InboundState FromState,
    InboundState ToState,
    string Operator,
    string Reason,
    DateTimeOffset OccurredAt);

public sealed class InboundOrder
{
    private readonly List<InboundLine> _lines = [];
    private readonly List<InboundStateHistory> _stateHistory = [];

    private InboundOrder()
    {
    }

    public InboundOrder(string orderNumber, DateTimeOffset? createdAt = null)
    {
        Id = Guid.NewGuid();
        OrderNumber = Require(orderNumber, nameof(orderNumber));
        CreatedAt = Normalize(createdAt ?? DateTimeOffset.UtcNow);
        UpdatedAt = CreatedAt;
    }

    public Guid Id { get; private set; }

    public string OrderNumber { get; private set; } = null!;

    public string Number => OrderNumber;

    public InboundState State { get; private set; } = InboundState.Draft;

    public InboundState Status => State;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public string? CanceledBy { get; private set; }

    public string? CancellationReason { get; private set; }

    public IReadOnlyList<InboundLine> Lines => _lines.AsReadOnly();

    public IReadOnlyList<InboundStateHistory> StateHistory => _stateHistory.AsReadOnly();

    public InboundLine AddLine(InboundLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        EnsureState(InboundState.Draft, "Only a draft inbound order can add lines.");
        if (_lines.Any(existing => existing.Id == line.Id))
        {
            throw new InvalidOperationException($"Inbound line '{line.Id}' already belongs to order '{OrderNumber}'.");
        }

        _lines.Add(line);
        Touch();
        return line;
    }

    public InboundLine AddLine(
        Guid materialId,
        decimal orderedQuantity,
        string? batchNumber = null,
        DateOnly? expirationDate = null)
        => AddLine(new InboundLine(materialId, orderedQuantity, batchNumber, expirationDate));

    public void TransitionTo(
        InboundState nextState,
        string @operator,
        string reason,
        DateTimeOffset? occurredAt = null)
    {
        var timestamp = Normalize(occurredAt ?? DateTimeOffset.UtcNow);
        var normalizedOperator = Require(@operator, nameof(@operator));
        var normalizedReason = Require(reason, nameof(reason));
        if (!CanTransition(State, nextState))
        {
            throw new InvalidOperationException(
                $"Inbound state transition '{State}' -> '{nextState}' is not allowed.");
        }

        var previous = State;
        State = nextState;
        UpdatedAt = timestamp;
        _stateHistory.Add(new InboundStateHistory(
            Guid.NewGuid(), Id, previous, nextState, normalizedOperator, normalizedReason, timestamp));
    }

    public void Cancel(string @operator, string reason, DateTimeOffset? occurredAt = null)
    {
        TransitionTo(InboundState.Canceled, @operator, reason, occurredAt);
        CanceledBy = Require(@operator, nameof(@operator));
        CancellationReason = Require(reason, nameof(reason));
    }

    public void MarkException(string @operator, string reason, DateTimeOffset? occurredAt = null)
        => TransitionTo(InboundState.Exception, @operator, reason, occurredAt);

    public bool IsFullyReceived
        => _lines.Count > 0 && _lines.All(line => line.RemainingQuantity == 0m);

    public decimal TotalOrderedQuantity => _lines.Sum(line => line.OrderedQuantity);

    public decimal TotalReceivedQuantity => _lines.Sum(line => line.ReceivedQuantity);

    public void EnsureCanReceive()
    {
        if (State is not (InboundState.Draft or InboundState.Receiving))
        {
            throw new InvalidOperationException($"Inbound order '{OrderNumber}' cannot receive in state '{State}'.");
        }

        if (_lines.Count == 0)
        {
            throw new InvalidOperationException($"Inbound order '{OrderNumber}' has no lines.");
        }
    }

    public void StartReceiving(string @operator, DateTimeOffset? occurredAt = null)
    {
        if (State == InboundState.Draft)
        {
            TransitionTo(InboundState.Receiving, @operator, "收货开始", occurredAt);
        }
    }

    public void CompleteReceiving(string @operator, DateTimeOffset? occurredAt = null)
    {
        if (IsFullyReceived && State == InboundState.Receiving)
        {
            TransitionTo(InboundState.Received, @operator, "明细已全部收货", occurredAt);
        }
    }

    private void EnsureState(InboundState expected, string message)
    {
        if (State != expected)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static bool CanTransition(InboundState from, InboundState to)
        => from switch
        {
            InboundState.Draft => to is InboundState.Receiving or InboundState.Canceled,
            InboundState.Receiving => to is InboundState.Receiving or InboundState.Received or InboundState.Canceled or InboundState.Exception,
            InboundState.Received => to is InboundState.PutawayQueued or InboundState.Canceled or InboundState.Exception,
            InboundState.PutawayQueued => to is InboundState.Completed or InboundState.Canceled or InboundState.Exception,
            InboundState.Exception => to is InboundState.PutawayQueued or InboundState.Canceled,
            _ => false
        };

    private void Touch() => UpdatedAt = Normalize(DateTimeOffset.UtcNow);

    private static string Require(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty value is required.", parameterName);
        }

        return value.Trim();
    }

    private static DateTimeOffset Normalize(DateTimeOffset value) => value.ToUniversalTime();
}
