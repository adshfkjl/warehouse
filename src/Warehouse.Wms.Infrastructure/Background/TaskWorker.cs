using Warehouse.Wms.Application.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WmsTaskScheduler = Warehouse.Wms.Application.Tasks.TaskScheduler;

namespace Warehouse.Wms.Infrastructure.Background;

public sealed class TaskWorker
{
    private readonly WmsTaskScheduler _scheduler;
    private readonly TimeSpan _pollInterval;
    private readonly WorkflowRecoveryService? _workflowRecovery;
    private bool _recoveryCompleted;

    public TaskWorker(WmsTaskScheduler scheduler, TimeSpan? pollInterval = null, WorkflowRecoveryService? workflowRecovery = null)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
        _workflowRecovery = workflowRecovery;
        if (_pollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(pollInterval), _pollInterval, "Poll interval must be positive.");
        }
    }

    public TimeSpan PollInterval => _pollInterval;

    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        if (!_recoveryCompleted)
        {
            if (_workflowRecovery is not null)
            {
                foreach (var workflow in new[] { "Inbound", "Outbound", "Transfer", "Stocktaking", "Putaway", "Relocation" })
                    await _workflowRecovery.RecoverAsync(workflow, cancellationToken);
            }
            await _scheduler.RecoverPersistedInFlightAsync(cancellationToken);
            _recoveryCompleted = true;
        }
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

public sealed class TaskWorkerHostedService(TaskWorker worker, ILogger<TaskWorkerHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await worker.RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
#pragma warning disable CA1848
                logger.LogError(exception, "Task worker iteration failed; backing off before retry.");
#pragma warning restore CA1848
                await Task.Delay(worker.PollInterval, stoppingToken);
                continue;
            }

            await Task.Delay(worker.PollInterval, stoppingToken);
        }
    }
}
