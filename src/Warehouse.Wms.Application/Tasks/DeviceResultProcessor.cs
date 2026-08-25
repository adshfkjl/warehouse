using Warehouse.Wms.Domain.Devices;

namespace Warehouse.Wms.Application.Tasks;

public sealed class DeviceResultProcessor(TaskScheduler scheduler)
{
    private readonly TaskScheduler _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));

    public Task<bool> ProcessAsync(
        TaskDispatchRequest request,
        DeviceResultObservation observation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _scheduler.ApplyObservationAsync(request, observation);
    }
}
