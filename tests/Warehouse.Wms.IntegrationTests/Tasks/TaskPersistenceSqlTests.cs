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

    [SqlServerFact]
    public async Task Sql_schedulers_dispatch_same_device_concurrently_without_duplicate_submit_and_record_contention()
    {
        var configuredConnection = Environment.GetEnvironmentVariable("WMS_SQLSERVER_TEST_CONNECTION")!;
        var connectionBuilder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(configuredConnection)
        {
            InitialCatalog = $"WmsSchedulerConcurrent_{Guid.NewGuid():N}"
        };
        var options = new DbContextOptionsBuilder<WarehouseDbContext>().UseSqlServer(connectionBuilder.ConnectionString).Options;
        await using var setupFactory = new TestDbContextFactory(options);
        await using (var setup = await setupFactory.CreateDbContextAsync()) await setup.Database.MigrateAsync();

        await using var firstFactory = new TestDbContextFactory(options);
        await using var secondFactory = new TestDbContextFactory(options);
        var firstStore = new SqlServerTaskPersistenceStore(firstFactory);
        var secondStore = new SqlServerTaskPersistenceStore(secondFactory);
        var firstGateway = new SchedulerGateway();
        var secondGateway = new SchedulerGateway();
        var first = new WmsTaskScheduler(firstGateway, persistenceStore: firstStore, workerId: "concurrent-worker-1");
        var second = new WmsTaskScheduler(secondGateway, persistenceStore: secondStore, workerId: "concurrent-worker-2");

        await first.EnqueueAsync(new TaskDispatchRequest(
                new WarehouseTask("TASK-SQL-CONCURRENT-001", "Putaway"),
                new DeviceTask("sql-concurrent-idem-1", "TASK-SQL-CONCURRENT-001", "PLC-CONCURRENT-01", "1-1", "2-1", "LP-01", "v1"),
                DeviceOperationKind.Inbound));
        await second.EnqueueAsync(new TaskDispatchRequest(
                new WarehouseTask("TASK-SQL-CONCURRENT-002", "Putaway"),
                new DeviceTask("sql-concurrent-idem-2", "TASK-SQL-CONCURRENT-002", "PLC-CONCURRENT-01", "1-2", "2-2", "LP-02", "v1"),
                DeviceOperationKind.Inbound));

        var results = await Task.WhenAll(first.DispatchNextAsync(), second.DispatchNextAsync());

        Assert.Single(results.Where(result => result is not null));
        Assert.Equal(1, firstGateway.SubmitCount + secondGateway.SubmitCount);
        var activeLocks = await firstStore.GetActiveResourceLocksAsync();
        Assert.Single(activeLocks.Where(item => item.ResourceType == "Device" && item.ResourceId == "PLC-CONCURRENT-01"));
        Assert.True(firstStore.LockContentionCount + secondStore.LockContentionCount >= 1);
    }

    [SqlServerFact]
    public async Task Sql_schedulers_dispatch_different_devices_concurrently_once_each_and_preserve_physical_unknown()
    {
        var configuredConnection = Environment.GetEnvironmentVariable("WMS_SQLSERVER_TEST_CONNECTION")!;
        var connectionBuilder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(configuredConnection)
        {
            InitialCatalog = $"WmsSchedulerDevices_{Guid.NewGuid():N}"
        };
        var options = new DbContextOptionsBuilder<WarehouseDbContext>().UseSqlServer(connectionBuilder.ConnectionString).Options;
        await using var setupFactory = new TestDbContextFactory(options);
        await using (var setup = await setupFactory.CreateDbContextAsync()) await setup.Database.MigrateAsync();

        await using var firstFactory = new TestDbContextFactory(options);
        await using var secondFactory = new TestDbContextFactory(options);
        var firstStore = new SqlServerTaskPersistenceStore(firstFactory);
        var secondStore = new SqlServerTaskPersistenceStore(secondFactory);
        var probe = new SubmitConcurrencyProbe();
        var firstGateway = new SchedulerGateway(probe: probe);
        var secondGateway = new SchedulerGateway(probe: probe);
        var first = new WmsTaskScheduler(firstGateway, persistenceStore: firstStore, workerId: "devices-worker-1");
        var second = new WmsTaskScheduler(secondGateway, persistenceStore: secondStore, workerId: "devices-worker-2");

        var firstRequest = new TaskDispatchRequest(
            new WarehouseTask("TASK-SQL-DEVICES-001", "Putaway"),
            new DeviceTask("sql-devices-idem-1", "TASK-SQL-DEVICES-001", "PLC-DEVICES-01", "1-1", "2-1", "LP-01", "v1"),
            DeviceOperationKind.Inbound);
        var secondRequest = new TaskDispatchRequest(
            new WarehouseTask("TASK-SQL-DEVICES-002", "Putaway"),
            new DeviceTask("sql-devices-idem-2", "TASK-SQL-DEVICES-002", "PLC-DEVICES-02", "1-2", "2-2", "LP-02", "v1"),
            DeviceOperationKind.Inbound);
        await first.EnqueueAsync(firstRequest);
        await second.EnqueueAsync(secondRequest);

        var results = await Task.WhenAll(first.DispatchNextAsync(), second.DispatchNextAsync());
        Assert.All(results, result => Assert.NotNull(result));
        Assert.Equal(1, firstGateway.SubmitCount);
        Assert.Equal(1, secondGateway.SubmitCount);
        Assert.True(probe.MaxConcurrentSubmits >= 2);

        var unknownGateway = new SchedulerGateway(DeviceOperationStatus.PhysicalStateUnknown);
        var unknownStore = new SqlServerTaskPersistenceStore(firstFactory);
        var unknownScheduler = new WmsTaskScheduler(unknownGateway, persistenceStore: unknownStore);
        var unknownRequest = new TaskDispatchRequest(
            new WarehouseTask("TASK-SQL-UNKNOWN-001", "Putaway"),
            new DeviceTask("sql-unknown-idem-1", "TASK-SQL-UNKNOWN-001", "PLC-UNKNOWN-01", "1-3", "2-3", "LP-03", "v1"),
            DeviceOperationKind.Inbound);
        await unknownScheduler.EnqueueAsync(unknownRequest);
        var unknownResult = await unknownScheduler.DispatchNextAsync();
        Assert.Equal(DeviceOperationStatus.PhysicalStateUnknown, unknownResult!.Status);
        Assert.Null(await unknownScheduler.DispatchNextAsync());
        var persistedUnknown = await unknownStore.GetTaskAsync(unknownRequest.Task.TaskNumber);
        Assert.Equal(TaskState.PhysicalStateUnknown, persistedUnknown!.State);
        Assert.Equal(1, unknownGateway.SubmitCount);
    }

    [Fact]
    public async Task Sql_lock_retry_is_capped_and_cancellation_is_observed()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SqlServerTaskPersistenceStore.ExecuteWithTransientRetryAsync(
                () => { attempts++; return Task.FromException(new InvalidOperationException("deadlock")); },
                CancellationToken.None,
                _ => true));
        Assert.Equal(3, attempts);

        using var cancellation = new CancellationTokenSource();
        attempts = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SqlServerTaskPersistenceStore.ExecuteWithTransientRetryAsync(
                () =>
                {
                    attempts++;
                    if (attempts == 1) cancellation.Cancel();
                    return Task.FromException(new InvalidOperationException("deadlock"));
                },
                cancellation.Token,
                _ => true));
        Assert.Equal(1, attempts);
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
        private readonly DeviceOperationStatus _submitStatus;
        private readonly SubmitConcurrencyProbe? _probe;
        private int _submitCount;
        private int _queryCount;
        private int _activeSubmits;
        private int _maxConcurrentSubmits;

        public SchedulerGateway(DeviceOperationStatus submitStatus = DeviceOperationStatus.Accepted, SubmitConcurrencyProbe? probe = null)
        {
            _submitStatus = submitStatus;
            _probe = probe;
        }

        public int SubmitCount => Volatile.Read(ref _submitCount);
        public int QueryCount => Volatile.Read(ref _queryCount);
        public int MaxConcurrentSubmits => Volatile.Read(ref _maxConcurrentSubmits);

        public async Task<DeviceOperationResult> SubmitInboundAsync(DeviceTask task, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _submitCount);
            var active = Interlocked.Increment(ref _activeSubmits);
            while (true)
            {
                var observed = Volatile.Read(ref _maxConcurrentSubmits);
                if (active <= observed || Interlocked.CompareExchange(ref _maxConcurrentSubmits, active, observed) == observed) break;
            }

            _probe?.Enter();

            try
            {
                if (_probe is not null)
                {
                    await _probe.WaitForPairAsync(cancellationToken);
                }
                else
                {
                    await Task.Delay(25, cancellationToken);
                }
                return new DeviceOperationResult(task.IdempotencyKey, _submitStatus, "device-task-001");
            }
            finally
            {
                _probe?.Exit();
                Interlocked.Decrement(ref _activeSubmits);
            }
        }

        public Task<DeviceOperationResult> SubmitOutboundAsync(DeviceTask task, CancellationToken cancellationToken = default)
            => SubmitInboundAsync(task, cancellationToken);

        public Task<DeviceOperationResult> SubmitTransferAsync(DeviceTask task, CancellationToken cancellationToken = default)
            => SubmitInboundAsync(task, cancellationToken);

        public Task<DeviceResultObservation?> GetStatusAsync(string deviceTaskNumber, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _queryCount);
            return Task.FromResult<DeviceResultObservation?>(
                new DeviceResultObservation(deviceTaskNumber, 1, DeviceOperationStatus.Executing, DeviceObservationSource.Polling, DateTimeOffset.UtcNow));
        }

        public Task<DeviceOperationResult> TestConnectionAsync(string deviceId, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeviceOperationResult(deviceId, DeviceOperationStatus.Succeeded));

        public Task<DeviceOperationResult> RequestStopAsync(DeviceTask task, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeviceOperationResult(task.IdempotencyKey, DeviceOperationStatus.StopConfirmed));
    }

    private sealed class SubmitConcurrencyProbe
    {
        private int _active;
        private int _max;
        private readonly TaskCompletionSource _pairReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int MaxConcurrentSubmits => Volatile.Read(ref _max);
        public void Enter()
        {
            var active = Interlocked.Increment(ref _active);
            while (true)
            {
                var observed = Volatile.Read(ref _max);
                if (active <= observed || Interlocked.CompareExchange(ref _max, active, observed) == observed) break;
            }

            if (active >= 2)
            {
                _pairReached.TrySetResult();
            }
        }
        public void Exit() => Interlocked.Decrement(ref _active);
        public Task WaitForPairAsync(CancellationToken cancellationToken)
            => _pairReached.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
    }
}
