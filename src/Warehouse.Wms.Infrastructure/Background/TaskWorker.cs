using Warehouse.Wms.Application.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using WmsTaskScheduler = Warehouse.Wms.Application.Tasks.TaskScheduler;
using Warehouse.Wms.Application.Inbound;
using Warehouse.Wms.Application.Outbound;
using Warehouse.Wms.Domain.Tasks;
using Warehouse.Wms.Application.Relocation;
using Warehouse.Wms.Application.Stocktaking;

namespace Warehouse.Wms.Infrastructure.Background;

public sealed class TaskWorker
{
    private readonly WmsTaskScheduler _scheduler;
    private readonly TimeSpan _pollInterval;
    private readonly WorkflowRecoveryService? _workflowRecovery;
    private readonly InboundOrderService? _inboundOrders;
    private readonly OutboundReviewService? _outboundReviews;
    private readonly OutboundTaskService? _outboundTasks;
    private readonly PutawayTaskService? _putawayTasks;
    private readonly ITaskPersistenceStore? _taskPersistence;
    private readonly IBusinessWorkflowStore? _businessWorkflows;
    private readonly RelocationService? _relocations;
    private readonly StocktakingService? _stocktaking;
    private readonly IServiceScopeFactory? _scopeFactory;
    private bool _recoveryCompleted;

    public TaskWorker(WmsTaskScheduler scheduler, TimeSpan? pollInterval = null, WorkflowRecoveryService? workflowRecovery = null, InboundOrderService? inboundOrders = null, OutboundReviewService? outboundReviews = null, OutboundTaskService? outboundTasks = null, PutawayTaskService? putawayTasks = null, ITaskPersistenceStore? taskPersistence = null, IBusinessWorkflowStore? businessWorkflows = null, RelocationService? relocations = null, StocktakingService? stocktaking = null, IServiceScopeFactory? scopeFactory = null)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
        _workflowRecovery = workflowRecovery;
        _inboundOrders = inboundOrders;
        _outboundReviews = outboundReviews;
        _outboundTasks = outboundTasks;
        _putawayTasks = putawayTasks;
        _taskPersistence = taskPersistence;
        _businessWorkflows = businessWorkflows;
        _relocations = relocations;
        _stocktaking = stocktaking;
        _scopeFactory = scopeFactory;
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
            if (_inboundOrders is not null)
                await _inboundOrders.RestoreAsync(cancellationToken);
            if (_outboundTasks is not null)
                await _outboundTasks.RestoreAsync(cancellationToken);
            if (_putawayTasks is not null)
                await _putawayTasks.RestoreAsync(cancellationToken);
            if (_outboundReviews is not null)
                await _outboundReviews.RestoreAsync(cancellationToken);
            if (_relocations is not null)
                await _relocations.RestoreAsync(cancellationToken);
            if (_stocktaking is not null)
                await _stocktaking.RestoreAsync(cancellationToken);
            if (_scopeFactory is not null)
            {
                using var scope = _scopeFactory.CreateScope();
                var differences = scope.ServiceProvider.GetService<StocktakingDifferenceService>();
                if (differences is not null)
                    await differences.RestoreAsync(cancellationToken);
            }
            if (_workflowRecovery is not null)
            {
                foreach (var workflow in new[] { "Inbound", "Outbound", "Transfer", "Stocktaking", "Putaway", "Relocation" })
                    await _workflowRecovery.RecoverAsync(workflow, cancellationToken);
            }
            await BlockMissingBusinessSnapshotsAsync(cancellationToken);
            await _scheduler.RecoverPersistedInFlightAsync(cancellationToken);
            _recoveryCompleted = true;
        }
        await _scheduler.RecoverInFlightAsync(cancellationToken);
        while (await _scheduler.DispatchNextAsync(cancellationToken) is not null)
        {
        }
    }

    private async Task BlockMissingBusinessSnapshotsAsync(CancellationToken cancellationToken)
    {
        if (_taskPersistence is null || _businessWorkflows is null) return;
        var open = await _taskPersistence.GetTasksByStatesAsync(
            [TaskState.Created, TaskState.Allocated, TaskState.Queued, TaskState.Dispatching, TaskState.SentToPlc, TaskState.Executing], cancellationToken);
        foreach (var task in open)
        {
            var kind = task.WorkflowKind?.Trim();
            if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(task.WorkflowReference)) continue;
            var aggregateType = task.TaskType.Contains("Stocktaking", StringComparison.OrdinalIgnoreCase) ? "Stocktaking" :
                kind.Equals("Outbound", StringComparison.OrdinalIgnoreCase) ? "OutboundTask" :
                kind.Equals("Inbound", StringComparison.OrdinalIgnoreCase)
                    ? (task.TaskType.Equals("Putaway", StringComparison.OrdinalIgnoreCase) ? "PutawayTask" : "InboundOrder")
                    : kind.Equals("Relocation", StringComparison.OrdinalIgnoreCase) || kind.Equals("Transfer", StringComparison.OrdinalIgnoreCase) ? "Relocation" :
                    kind.Equals("Stocktaking", StringComparison.OrdinalIgnoreCase) ? "Stocktaking" : null;
            if (aggregateType is null) continue;
            var snapshots = await _businessWorkflows.GetByTypeAsync(aggregateType, cancellationToken);
            var found = snapshots.Any(item =>
            {
                if (!string.Equals(item.AggregateKey, task.WorkflowReference, StringComparison.Ordinal)
                    && !string.Equals(item.WarehouseTaskNumber, task.WorkflowReference, StringComparison.Ordinal)) return false;
                try
                {
                    using var json = System.Text.Json.JsonDocument.Parse(item.SnapshotJson);
                    return json.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object;
                }
                catch (System.Text.Json.JsonException) { return false; }
            });
            if (!found)
                await _taskPersistence.MarkWorkflowRecoveryBlockedAsync(task.TaskNumber, "BUSINESS_SNAPSHOT_MISSING", cancellationToken);
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
