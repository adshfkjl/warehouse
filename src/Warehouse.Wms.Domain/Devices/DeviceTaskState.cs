namespace Warehouse.Wms.Domain.Devices;

public enum DeviceTaskState
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
