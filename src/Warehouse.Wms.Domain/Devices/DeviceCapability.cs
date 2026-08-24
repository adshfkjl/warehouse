namespace Warehouse.Wms.Domain.Devices;

[Flags]
public enum DeviceCapability
{
    None = 0,
    TaskKeyDeduplication = 1,
    TaskQuery = 2,
    StopControl = 4,
    CompletionCallback = 8
}
