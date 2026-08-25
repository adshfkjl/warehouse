using Warehouse.Wms.Application.Tasks;
using WmsTaskScheduler = Warehouse.Wms.Application.Tasks.TaskScheduler;

namespace Warehouse.Wms.Infrastructure.Background;

public sealed class TaskWorker
{
    private readonly WmsTaskScheduler _scheduler;
    private readonly TimeSpan _pollInterval;

    public TaskWorker(WmsTaskScheduler scheduler, TimeSpan? pollInterval = null)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
        if (_pollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval), _pollInterval, "Poll interval must be positive.");
        }
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        await _scheduler.RecoverInFlightAsync(cancellationToken);
        while (await _scheduler.DispatchNextAsync(cancellationToken) is not null)
        {
        }
    }

    public async Task RunAsync(CancellationToken stoppingToken = default)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken);
            await Task.Delay(_pollInterval, stoppingToken);
        }
    }
}
