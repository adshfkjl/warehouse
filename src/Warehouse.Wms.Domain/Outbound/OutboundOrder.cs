namespace Warehouse.Wms.Domain.Outbound;

public sealed class OutboundOrder
{
    private readonly List<OutboundLine> _lines = [];

    public OutboundOrder(string orderNumber)
    {
        if (string.IsNullOrWhiteSpace(orderNumber)) throw new ArgumentException("An order number is required.", nameof(orderNumber));
        Id = Guid.NewGuid();
        OrderNumber = orderNumber.Trim();
    }

    public Guid Id { get; }
    public string OrderNumber { get; }
    public OutboundState State { get; private set; } = OutboundState.Draft;
    public IReadOnlyList<OutboundLine> Lines => _lines.AsReadOnly();

    public OutboundLine AddLine(OutboundLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (State != OutboundState.Draft) throw new InvalidOperationException("Only a draft order can add lines.");
        _lines.Add(line);
        return line;
    }

    public void TransitionTo(OutboundState next)
    {
        if (State is OutboundState.Completed or OutboundState.Canceled) throw new InvalidOperationException("The outbound order is terminal.");
        var allowed = State switch
        {
            OutboundState.Draft => next is OutboundState.Allocated or OutboundState.Canceled,
            OutboundState.Allocated => next is OutboundState.Locked or OutboundState.Canceled or OutboundState.Exception,
            OutboundState.Locked => next is OutboundState.Picking or OutboundState.Canceled or OutboundState.Exception,
            OutboundState.Picking => next is OutboundState.AwaitingReview or OutboundState.Exception,
            OutboundState.AwaitingReview => next is OutboundState.Completed or OutboundState.Exception,
            OutboundState.Exception => next is OutboundState.Allocated or OutboundState.Canceled,
            _ => false
        };
        if (!allowed) throw new InvalidOperationException($"Outbound state transition '{State}' -> '{next}' is not allowed.");
        State = next;
    }
}
