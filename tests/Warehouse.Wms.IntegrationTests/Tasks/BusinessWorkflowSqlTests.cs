using Microsoft.EntityFrameworkCore;
using Warehouse.Wms.Application.Tasks;
using Warehouse.Wms.Application.Inbound;
using Warehouse.Wms.Infrastructure.Persistence;

namespace Warehouse.Wms.IntegrationTests.Tasks;

public sealed class BusinessWorkflowSqlTests
{
    [SqlServerFact]
    public async Task Sql_business_workflow_round_trips_after_restart_and_enforces_version_and_idempotency()
    {
        var configured = Environment.GetEnvironmentVariable("WMS_SQLSERVER_TEST_CONNECTION")!;
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = $"WmsBusinessWorkflow_{Guid.NewGuid():N}"
        };
        var options = new DbContextOptionsBuilder<WarehouseDbContext>().UseSqlServer(builder.ConnectionString).Options;
        await using var factory = new TestDbContextFactory(options);
        await using (var setup = await factory.CreateDbContextAsync()) await setup.Database.MigrateAsync();

        var store = new SqlServerBusinessWorkflowStore(factory);
        var first = new BusinessWorkflowSnapshot("InboundOrder", "IN-SQL-001", 1, "Received", "{\"id\":1}", DateTimeOffset.UtcNow, "TASK-IN-SQL");
        await store.SaveAsync(first, 0);
        await Assert.ThrowsAsync<BusinessWorkflowConcurrencyException>(() => store.SaveAsync(first with { Version = 2 }, 0));
        var second = first with { Version = 2, Status = "PutawayQueued", SnapshotJson = "{\"id\":2}" };
        await store.SaveAsync(second, 1);
        var registered = await store.RegisterIdempotencyAsync("inbound-receipt", "receipt-sql-001", "hash-1", "InboundOrder", "IN-SQL-001");
        var replay = await new SqlServerBusinessWorkflowStore(factory).RegisterIdempotencyAsync("inbound-receipt", "receipt-sql-001", "hash-1", "InboundOrder", "IN-SQL-001");
        var restored = await new SqlServerBusinessWorkflowStore(factory).GetAsync("InboundOrder", "IN-SQL-001");
        var history = await store.GetHistoryAsync("InboundOrder", "IN-SQL-001");

        Assert.False(registered.Replayed);
        Assert.True(replay.Replayed);
        Assert.Equal(2, restored!.Version);
        Assert.Equal("PutawayQueued", restored.Status);
        Assert.Single(history);
        Assert.Equal("Received", history[0].FromStatus);
    }

    [SqlServerFact]
    public async Task Sql_inbound_service_rebuilds_receipt_idempotency_after_restart()
    {
        var configured = Environment.GetEnvironmentVariable("WMS_SQLSERVER_TEST_CONNECTION")!;
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(configured) { InitialCatalog = $"WmsInboundRecovery_{Guid.NewGuid():N}" };
        var options = new DbContextOptionsBuilder<WarehouseDbContext>().UseSqlServer(builder.ConnectionString).Options;
        await using var factory = new TestDbContextFactory(options);
        await using (var setup = await factory.CreateDbContextAsync()) await setup.Database.MigrateAsync();
        var store = new SqlServerBusinessWorkflowStore(factory);
        var material = Guid.NewGuid();
        var first = new InboundOrderService(store);
        var order = first.Create("IN-SQL-RECOVERY", [new InboundLineRequest(material, 2m)]);
        var request = new InboundReceiptRequest("receipt-sql-recovery", 2m, PalletCode: "PLT-SQL-RECOVERY");
        var original = first.Receive(order.OrderNumber, order.Lines.Single().Id, request);

        var restarted = new InboundOrderService(new SqlServerBusinessWorkflowStore(factory));
        await restarted.RestoreAsync();
        var replay = restarted.Receive(order.OrderNumber, request);

        Assert.Equal(original.Quantity, replay.Quantity);
        Assert.Single(restarted.PendingInboundInventory);
        Assert.Single(restarted.Get(order.OrderNumber).Lines.Single().Receipts);
    }

    private sealed class SqlServerFactAttribute : FactAttribute
    {
        public SqlServerFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WMS_SQLSERVER_TEST_CONNECTION")))
                Skip = "Set WMS_SQLSERVER_TEST_CONNECTION to run SQL Server business workflow tests.";
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<WarehouseDbContext> options) : IDbContextFactory<WarehouseDbContext>, IAsyncDisposable
    {
        public WarehouseDbContext CreateDbContext() => new(options);
        public Task<WarehouseDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WarehouseDbContext(options));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
