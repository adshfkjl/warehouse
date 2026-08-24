using Warehouse.Wms.Domain.Devices;

namespace Warehouse.Wms.Application.Devices;

public interface IWarehouseDeviceGateway
{
    Task<DeviceOperationResult> SubmitInboundAsync(
        DeviceTask task,
        CancellationToken cancellationToken = default);

    Task<DeviceOperationResult> SubmitOutboundAsync(
        DeviceTask task,
        CancellationToken cancellationToken = default);

    Task<DeviceOperationResult> SubmitTransferAsync(
        DeviceTask task,
        CancellationToken cancellationToken = default);

    Task<DeviceResultObservation?> GetStatusAsync(
        string deviceTaskNumber,
        CancellationToken cancellationToken = default);

    Task<DeviceOperationResult> TestConnectionAsync(
        string deviceId,
        CancellationToken cancellationToken = default);

    Task<DeviceOperationResult> RequestStopAsync(
        DeviceTask task,
        CancellationToken cancellationToken = default);
}
