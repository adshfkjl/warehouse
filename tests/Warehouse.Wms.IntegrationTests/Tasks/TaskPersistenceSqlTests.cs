using Microsoft.EntityFrameworkCore;
using Warehouse.Wms.Domain.Tasks;
using Warehouse.Wms.Infrastructure.Persistence;

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
}
