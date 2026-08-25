using Microsoft.EntityFrameworkCore;
using Warehouse.Wms.Application.Inventory;
using Warehouse.Wms.Domain.Inventory;
using Warehouse.Wms.Infrastructure.Persistence;

namespace Warehouse.Wms.IntegrationTests.Inventory;

public sealed class InventorySqlServerPersistenceTests
{
    [SqlServerFact]
    public async Task Sql_server_ledger_survives_restart_and_rejects_fingerprint_conflict()
    {
        var connectionString = Environment.GetEnvironmentVariable("WMS_SQLSERVER_TEST_CONNECTION");
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        var options = new DbContextOptionsBuilder<WarehouseDbContext>()
            .UseSqlServer(connectionString)
            .Options;
        await using var factory = new TestDbContextFactory(options);
        await using (var setup = await factory.CreateDbContextAsync())
        {
            await setup.Database.MigrateAsync();
            await setup.Database.ExecuteSqlRawAsync("DELETE FROM InventoryTransactions; DELETE FROM InventoryBalances;");
        }

        var store = new SqlServerInventoryLedgerStore(factory);
        var materialId = Guid.NewGuid();
        var service = new InventoryService(store);
        var context = new InventoryOperationContext("sql-receive-1", operatorId: "integration");
        var first = await service.IncreaseAsync(materialId, null, null, null, 2m, 20m, context);
        var replay = await service.IncreaseAsync(materialId, null, null, null, 2m, 20m, context);
        Assert.Equal(first.Id, replay.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.IncreaseAsync(materialId, null, null, null, 3m, 30m, context));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DecreaseAsync(materialId, null, null, null, 3m, 30m, new InventoryOperationContext("sql-overdraw")));

        var adjustment = await service.AdjustAsync(
            materialId,
            null,
            null,
            null,
            -1m,
            -10m,
            new InventoryOperationContext("sql-adjust-1", operatorId: "supervisor", reason: "stocktake variance"));
        Assert.Equal(-1m, adjustment.Quantity);

        var restarted = new InventoryService(store);
        Assert.Equal(1m, restarted.GetBalance(materialId, null, null, null)!.Quantity);
        Assert.Equal(3, restarted.GetBalance(materialId, null, null, null)!.Version);
        Assert.Equal(2, restarted.GetTransactions().Count);

        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(2, await verify.InventoryTransactions.CountAsync());
        var persistedBalance = await verify.InventoryBalances.SingleAsync();
        Assert.Equal(3, persistedBalance.Version);
    }

    private sealed class SqlServerFactAttribute : FactAttribute
    {
        public SqlServerFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WMS_SQLSERVER_TEST_CONNECTION")))
            {
                Skip = "Set WMS_SQLSERVER_TEST_CONNECTION to run the SQL Server persistence fixture.";
            }
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<WarehouseDbContext> options)
        : IDbContextFactory<WarehouseDbContext>, IAsyncDisposable
    {
        public WarehouseDbContext CreateDbContext() => new(options);

        public Task<WarehouseDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new WarehouseDbContext(options));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
