using Microsoft.EntityFrameworkCore;
using Warehouse.Wms.Application.Devices;
using Warehouse.Wms.Application.Inventory;
using Warehouse.Wms.Application.Relocation;
using Warehouse.Wms.Application.Tasks;
using Warehouse.Wms.Domain.Devices;
using Warehouse.Wms.Domain.Inventory;
using Warehouse.Wms.Infrastructure.Persistence;
using WmsTaskScheduler = Warehouse.Wms.Application.Tasks.TaskScheduler;

namespace Warehouse.Wms.IntegrationTests.Relocation;

public sealed class RelocationSqlPersistenceTests
{
    [SqlServerFact]
    public async Task Relocation_snapshot_rehydrates_after_sql_restart()
    {
        var configured = Environment.GetEnvironmentVariable("WMS_SQLSERVER_TEST_CONNECTION")!;
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = $"WmsRelocation_{Guid.NewGuid():N}"
        };
        var options = new DbContextOptionsBuilder<WarehouseDbContext>().UseSqlServer(builder.ConnectionString).Options;
        await using var factory = new TestDbContextFactory(options);
        await using (var setup = await factory.CreateDbContextAsync()) await setup.Database.MigrateAsync();

        var store = new SqlServerBusinessWorkflowStore(factory);
        var inventory = new InventoryService();
        var material = Guid.NewGuid();
        var pallet = Guid.NewGuid();
        var source = Guid.NewGuid();
        var destination = Guid.NewGuid();
        await inventory.IncreaseAsync(material, pallet, source, "B-SQL", 1m, 1m, new InventoryOperationContext("seed"));

        var first = new RelocationService(inventory, new WmsTaskScheduler(new Gateway()), workflowStore: store);
        var created = await first.SubmitAsync(new RelocationRequest("rel-sql-restart", material, pallet, source, destination, 1m, 1m, "PLC-01", "B-SQL"));

        var restarted = new RelocationService(inventory, new WmsTaskScheduler(new Gateway()), workflowStore: new SqlServerBusinessWorkflowStore(factory));
        await restarted.RestoreAsync();
        var restored = restarted.Get("rel-sql-restart");

        Assert.Equal(created.Order.Id, restored.Order.Id);
        Assert.Equal(created.Task.TaskNumber, restored.Task.TaskNumber);
        Assert.Equal(created.Status, restored.Status);
        var replay = await restarted.SubmitAsync(new RelocationRequest("rel-sql-restart", material, pallet, source, destination, 1m, 1m, "PLC-01", "B-SQL"));
        Assert.Same(restored.Order, replay.Order);
    }

    private sealed class SqlServerFactAttribute : FactAttribute
    {
        public SqlServerFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WMS_SQLSERVER_TEST_CONNECTION")))
                Skip = "Set WMS_SQLSERVER_TEST_CONNECTION to run SQL Server relocation persistence tests.";
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<WarehouseDbContext> options)
        : IDbContextFactory<WarehouseDbContext>, IAsyncDisposable
    {
        public WarehouseDbContext CreateDbContext() => new(options);
        public Task<WarehouseDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WarehouseDbContext(options));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Gateway : IWarehouseDeviceGateway
    {
        public Task<DeviceOperationResult> SubmitInboundAsync(DeviceTask task, CancellationToken cancellationToken = default) => Accepted(task);
        public Task<DeviceOperationResult> SubmitOutboundAsync(DeviceTask task, CancellationToken cancellationToken = default) => Accepted(task);
        public Task<DeviceOperationResult> SubmitTransferAsync(DeviceTask task, CancellationToken cancellationToken = default) => Accepted(task);
        public Task<DeviceResultObservation?> GetStatusAsync(string deviceTaskNumber, CancellationToken cancellationToken = default) => Task.FromResult<DeviceResultObservation?>(null);
        public Task<DeviceOperationResult> TestConnectionAsync(string deviceId, CancellationToken cancellationToken = default) => Task.FromResult(new DeviceOperationResult($"connection:{deviceId}", DeviceOperationStatus.Succeeded));
        public Task<DeviceOperationResult> RequestStopAsync(DeviceTask task, CancellationToken cancellationToken = default) => Accepted(task);
        private static Task<DeviceOperationResult> Accepted(DeviceTask task) => Task.FromResult(new DeviceOperationResult(task.IdempotencyKey, DeviceOperationStatus.Accepted, $"sql-{task.WmsTaskId}"));
    }
}
