namespace Warehouse.Wms.Domain.Exceptions;

public enum ExceptionType
{
    Unknown,
    TaskFailed,
    DeviceTimeout,
    DeviceOffline,
    PhysicalStateUnknown,
    StopFailed,
    ResourceLockConflict,
    InventoryMismatch
}

public enum ExceptionSeverity
{
    Low,
    Medium,
    High,
    Critical
}

public enum ExceptionPhysicalState
{
    Unknown,
    Known,
    Confirmed,
    Failed
}

public enum ExceptionWorkItemStatus
{
    Active,
    InProgress,
    Resolved,
    Closed
}

public enum ExceptionAction
{
    Alerted,
    Merged,
    Retry,
    RequestStop,
    Reassign,
    ConfirmPhysicalResult,
    InventoryCorrection,
    Close,
    Reopened,
    ActionCompleted,
    ActionRejected
}
