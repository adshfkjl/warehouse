namespace Warehouse.Wms.Domain.Inventory;

public enum InventoryStatus
{
    Available,
    Locked,
    PendingInbound,
    PendingOutbound,
    Frozen,
    Exception
}

public enum InventoryTransactionType
{
    Increase,
    Decrease,
    Lock,
    Unlock,
    Move,
    Adjustment
}
