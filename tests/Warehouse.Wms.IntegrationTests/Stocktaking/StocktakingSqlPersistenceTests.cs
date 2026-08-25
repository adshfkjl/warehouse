using Microsoft.EntityFrameworkCore;
using Warehouse.Wms.Application.Devices;
using Warehouse.Wms.Application.Stocktaking;
using Warehouse.Wms.Application.Tasks;
using Warehouse.Wms.Domain.Devices;
using Warehouse.Wms.Domain.Inventory;
using Warehouse.Wms.Infrastructure.Persistence;
using WmsTaskScheduler = Warehouse.Wms.Application.Tasks.TaskScheduler;

namespace Warehouse.Wms.IntegrationTests.Stocktaking;

public sealed class StocktakingSqlPersistenceTests
{
    [SqlServerFact]
    public async Task Completing_stocktaking_releases_persistent_loading_point_lock()
    {
        var configured = Environment.GetEnvironmentVariable("WMS_SQLSERVER_TEST_CONNECTION");
        Assert.False(string.IsNullOrWhiteSpace(configured));
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = $"WmsStocktaking_{Guid.NewGuid():N}"
        };
        var options = new DbContextOptionsBuilder<WarehouseDbContext>().UseSqlServer(builder.ConnectionString).Options;
        await using var factory = new TestDbContextFactory(options);
        await using (var setup = await factory.CreateDbContextAsync())
            await setup.Database.MigrateAsync();

        var store = new SqlServerTaskPersistenceStore(factory);
        var service = new StocktakingService(
            [new StocktakingInventoryItem(
                "A-01", "A", "M-01", null, "P-01", Guid.NewGuid(), Guid.NewGuid(), 1m, 1m, InventoryStatus.Available)],
            new WmsTaskScheduler(new ScenarioGateway(), persistenceStore: store),
            store);
        var task = service.Create(new StocktakingRequest("ST-SQL-LOCK"));
        service.Start(task.TaskNumber);
        var item = task.Items.Single();
        await service.QueueDeviceTaskAsync(task.TaskNumber, item.Id, "PLC-01", "LP-SQL-01");

        Assert.NotEmpty(await store.GetActiveResourceLocksAsync());
        service.RecordCount(task.TaskNumber, item.Id, 1m, 1m, "LP-SQL-01");
        service.Complete(task.TaskNumber);

        Assert.Empty(await store.GetActiveResourceLocksAsync());
    }

    private sealed class SqlServerFactAttribute : FactAttribute
    {
        public SqlServerFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WMS_SQLSERVER_TEST_CONNECTION")))
                Skip = "Set WMS_SQLSERVER_TEST_CONNECTION to run the SQL Server stocktaking fixture.";
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<WarehouseDbContext> options)
        : IDbContextFactory<WarehouseDbContext>, IAsyncDisposable
    {
        public WarehouseDbContext CreateDbContext() => new(options);
        public Task<WarehouseDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new WarehouseDbContext(options));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ScenarioGateway : IWarehouseDeviceGateway
    {
        public Task<DeviceOperationResult> SubmitInboundAsync(DeviceTask task, CancellationToken cancellationToken = default)
            => Submit(task);
        public Task<DeviceOperationResult> SubmitOutboundAsync(DeviceTask task, CancellationToken cancellationToken = default)
            => Submit(task);
        public Task<DeviceOperationResult> SubmitTransferAsync(DeviceTask task, CancellationToken cancellationToken = default)
            => Submit(task);
        public Task<DeviceResultObservation?> GetStatusAsync(string deviceTaskNumber, CancellationToken cancellationToken = default)
            => Task.FromResult<DeviceResultObservation?>(null);
        public Task<DeviceOperationResult> TestConnectionAsync(string deviceId, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeviceOperationResult($"connection:{deviceId}", DeviceOperationStatus.Succeeded));
        public Task<DeviceOperationResult> RequestStopAsync(DeviceTask task, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeviceOperationResult(task.IdempotencyKey, DeviceOperationStatus.StopConfirmed));
        private static Task<DeviceOperationResult> Submit(DeviceTask task)
            => Task.FromResult(new DeviceOperationResult(task.IdempotencyKey, DeviceOperationStatus.Accepted, $"sim-{task.WmsTaskId}"));
    }
}
