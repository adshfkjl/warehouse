namespace Warehouse.Wms.Domain.Outbound;

public enum OutboundState
{
    Draft,
    Allocated,
    Locked,
    Picking,
    AwaitingReview,
    Completed,
    Canceled,
    Exception
}
