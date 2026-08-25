using Microsoft.EntityFrameworkCore;
using Warehouse.Wms.Application.Devices;
using Warehouse.Wms.Application.Tasks;
using Warehouse.Wms.Domain.Devices;
using Warehouse.Wms.Domain.Tasks;
using Warehouse.Wms.Infrastructure.Persistence;
using Warehouse.Wms.Infrastructure.Background;
using WmsTaskScheduler = Warehouse.Wms.Application.Tasks.TaskScheduler;

namespace Warehouse.Wms.IntegrationTests.Tasks;

public sealed class TaskPersistenceSqlTests
{
    [SqlServerFact]
    public async Task Sql_store_recovers_task_history_lock_and_idempotency_after_new_context()
    {
        var configuredConnection = Environment.GetEnvironmentVariable("WMS_SQLSERVER_TEST_CONNECTION")!;
        var connectionBuilder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(configuredConnection)
        {
            InitialCatalog = $"WmsTaskPersistence_{Guid.NewGuid():N}"
        };
        var connectionString = connectionBuilder.ConnectionString;
        await using var factory = new TestDbContextFactory(new DbContextOptionsBuilder<WarehouseDbContext>().UseSqlServer(connectionString).Options);
        await using (var setup = await factory.CreateDbContextAsync())
        {
            await setup.Database.MigrateAsync();
        }

        var store = new SqlServerTaskPersistenceStore(factory);
        var task = new WarehouseTask("TASK-SQL-001", "Putaway", new DateTimeOffset(2026, 8, 26, 1, 0, 0, TimeSpan.Zero));
        await store.CreateTaskAsync(task);
        await store.TransitionTaskAsync(task.TaskNumber, 1, TaskState.Allocated, "operator", "allocated");
        await store.RegisterIdempotencyKeyAsync(new TaskIdempotencyKey("device-command", "cmd-sql-001", "hash-sql", task.Id));
        var resourceLock = await store.AcquireResourceLockAsync("Location", "SQL-001", task.TaskNumber, task.CreatedAt, TimeSpan.FromMinutes(5));

        var restartedStore = new SqlServerTaskPersistenceStore(factory);
        var restored = await restartedStore.GetTaskAsync(task.TaskNumber);
        var idem = await restartedStore.GetIdempotencyKeyAsync("device-command", "cmd-sql-001");
        var locks = await restartedStore.GetActiveResourceLocksAsync(task.CreatedAt.AddMinutes(1));

        Assert.Equal(TaskState.Allocated, restored!.State);
        Assert.Single(restored.StateHistory);
        Assert.NotNull(idem);
        Assert.Contains(locks, x => x.Id == resourceLock.Id);
    }

    [SqlServerFact]
    public async Task Sql_scheduler_restart_reconciles_device_task_without_resubmitting()
    {
        var configuredConnection = Environment.GetEnvironmentVariable("WMS_SQLSERVER_TEST_CONNECTION")!;
        var connectionBuilder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(configuredConnection)
        {
            InitialCatalog = $"WmsScheduler_{Guid.NewGuid():N}"
        };
        var connectionString = connectionBuilder.ConnectionString;
        await using var factory = new TestDbContextFactory(new DbContextOptionsBuilder<WarehouseDbContext>().UseSqlServer(connectionString).Options);
        await using (var setup = await factory.CreateDbContextAsync())
            await setup.Database.MigrateAsync();

        var gateway = new SchedulerGateway();
        var store = new SqlServerTaskPersistenceStore(factory);
        var first = new WmsTaskScheduler(gateway, persistenceStore: store);
        var request = new TaskDispatchRequest(
            new WarehouseTask("TASK-SQL-SCHEDULER-001", "Putaway"),
            new DeviceTask("sql-scheduler-idem", "TASK-SQL-SCHEDULER-001", "PLC-01", "1-1", "2-1", "LP-01", "v1"),
            DeviceOperationKind.Inbound);

        await first.EnqueueAsync(request);
        var submitted = await first.DispatchNextAsync();
        Assert.NotNull(submitted);
        Assert.Equal(1, gateway.SubmitCount);

        var persisted = await store.GetTaskAsync(request.Task.TaskNumber);
        Assert.NotNull(persisted);
        Assert.Contains("device-task-001", persisted!.DispatchContextJson, StringComparison.Ordinal);
        Assert.Equal(WorkflowRecoveryStatus.RecoveredFromSnapshot, persisted.WorkflowRecoveryStatus);

        var restarted = new WmsTaskScheduler(gateway, persistenceStore: store);
        await new TaskWorker(restarted, workflowRecovery: new WorkflowRecoveryService(store)).RunOnceAsync();

        Assert.Equal(1, gateway.SubmitCount);
        Assert.True(gateway.QueryCount > 0);
        var recovered = await store.GetTaskAsync(request.Task.TaskNumber);
        Assert.Equal(WorkflowRecoveryStatus.RecoveredFromSnapshot, recovered!.WorkflowRecoveryStatus);
    }

    [SqlServerFact]
    public async Task Sql_schedulers_claim_same_device_exclusively_across_processes()
    {
        var configuredConnection = Environment.GetEnvironmentVariable("WMS_SQLSERVER_TEST_CONNECTION")!;
        var connectionBuilder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(configuredConnection)
        {
            InitialCatalog = $"WmsSchedulerLease_{Guid.NewGuid():N}"
        };
        var connectionString = connectionBuilder.ConnectionString;
        var options = new DbContextOptionsBuilder<WarehouseDbContext>().UseSqlServer(connectionString).Options;
        await using var setupFactory = new TestDbContextFactory(options);
        await using (var setup = await setupFactory.CreateDbContextAsync())
            await setup.Database.MigrateAsync();

        await using var firstFactory = new TestDbContextFactory(options);
        await using var secondFactory = new TestDbContextFactory(options);
        var firstStore = new SqlServerTaskPersistenceStore(firstFactory);
        var secondStore = new SqlServerTaskPersistenceStore(secondFactory);
        var firstGateway = new SchedulerGateway();
        var secondGateway = new SchedulerGateway();
        var first = new WmsTaskScheduler(firstGateway, persistenceStore: firstStore, workerId: "sql-worker-1");
        var second = new WmsTaskScheduler(secondGateway, persistenceStore: secondStore, workerId: "sql-worker-2");

        await first.EnqueueAsync(new TaskDispatchRequest(
            new WarehouseTask("TASK-SQL-LEASE-001", "Putaway"),
            new DeviceTask("sql-lease-idem-1", "TASK-SQL-LEASE-001", "PLC-LEASE-01", "1-1", "2-1", "LP-01", "v1"),
            DeviceOperationKind.Inbound));
        await second.EnqueueAsync(new TaskDispatchRequest(
            new WarehouseTask("TASK-SQL-LEASE-002", "Putaway"),
            new DeviceTask("sql-lease-idem-2", "TASK-SQL-LEASE-002", "PLC-LEASE-01", "1-2", "2-2", "LP-02", "v1"),
            DeviceOperationKind.Inbound));

        var firstResult = await first.DispatchNextAsync();
        var secondResult = await second.DispatchNextAsync();

        Assert.NotNull(firstResult);
        Assert.Null(secondResult);
        Assert.Equal(1, firstGateway.SubmitCount);
        Assert.Equal(0, secondGateway.SubmitCount);
        var activeLocks = await firstStore.GetActiveResourceLocksAsync();
        Assert.Contains(activeLocks, item => item.ResourceType == "Device" && item.ResourceId == "PLC-LEASE-01");
    }

    private sealed class SqlServerFactAttribute : FactAttribute
    {
        public SqlServerFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WMS_SQLSERVER_TEST_CONNECTION")))
            {
                Skip = "Set WMS_SQLSERVER_TEST_CONNECTION to run the SQL Server task persistence fixture.";
            }
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<WarehouseDbContext> options) : IDbContextFactory<WarehouseDbContext>, IAsyncDisposable
    {
        public WarehouseDbContext CreateDbContext() => new(options);
        public Task<WarehouseDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WarehouseDbContext(options));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SchedulerGateway : IWarehouseDeviceGateway
    {
        public int SubmitCount { get; private set; }
        public int QueryCount { get; private set; }

        public Task<DeviceOperationResult> SubmitInboundAsync(DeviceTask task, CancellationToken cancellationToken = default)
        {
            SubmitCount++;
            return Task.FromResult(new DeviceOperationResult(task.IdempotencyKey, DeviceOperationStatus.Accepted, "device-task-001"));
        }

        public Task<DeviceOperationResult> SubmitOutboundAsync(DeviceTask task, CancellationToken cancellationToken = default)
            => SubmitInboundAsync(task, cancellationToken);

        public Task<DeviceOperationResult> SubmitTransferAsync(DeviceTask task, CancellationToken cancellationToken = default)
            => SubmitInboundAsync(task, cancellationToken);

        public Task<DeviceResultObservation?> GetStatusAsync(string deviceTaskNumber, CancellationToken cancellationToken = default)
        {
            QueryCount++;
            return Task.FromResult<DeviceResultObservation?>(
                new DeviceResultObservation(deviceTaskNumber, 1, DeviceOperationStatus.Executing, DeviceObservationSource.Polling, DateTimeOffset.UtcNow));
        }

        public Task<DeviceOperationResult> TestConnectionAsync(string deviceId, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeviceOperationResult(deviceId, DeviceOperationStatus.Succeeded));

        public Task<DeviceOperationResult> RequestStopAsync(DeviceTask task, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeviceOperationResult(task.IdempotencyKey, DeviceOperationStatus.StopConfirmed));
    }
}
