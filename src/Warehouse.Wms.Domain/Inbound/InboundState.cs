namespace Warehouse.Wms.Domain.Inbound;

public enum InboundState
{
    Draft,
    Receiving,
    Received,
    PutawayQueued,
    Completed,
    Canceled,
    Exception
}
