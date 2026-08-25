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

    [SqlServerFact]
    public async Task Sql_concurrent_business_writers_allow_only_one_version_commit()
    {
        var configured = Environment.GetEnvironmentVariable("WMS_SQLSERVER_TEST_CONNECTION")!;
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(configured)
        {
            InitialCatalog = $"WmsBusinessConcurrency_{Guid.NewGuid():N}"
        };
        var options = new DbContextOptionsBuilder<WarehouseDbContext>().UseSqlServer(builder.ConnectionString).Options;
        await using var factory = new TestDbContextFactory(options);
        await using (var setup = await factory.CreateDbContextAsync()) await setup.Database.MigrateAsync();

        var first = new SqlServerBusinessWorkflowStore(factory);
        await first.SaveAsync(new BusinessWorkflowSnapshot("Relocation", "REL-CONCURRENT", 1, "Queued", "{\"v\":1}", DateTimeOffset.UtcNow), 0);
        var candidate = new BusinessWorkflowSnapshot("Relocation", "REL-CONCURRENT", 2, "Executing", "{\"v\":2}", DateTimeOffset.UtcNow);
        var attempts = await Task.WhenAll(
            TrySaveAsync(new SqlServerBusinessWorkflowStore(factory), candidate),
            TrySaveAsync(new SqlServerBusinessWorkflowStore(factory), candidate));

        Assert.Equal(1, attempts.Count(result => result));
        Assert.Equal(1, attempts.Count(result => !result));
    }

    [SqlServerFact]
    public async Task Sql_concurrent_idempotency_registration_replays_one_entry_and_rejects_hash_conflict()
    {
        var configured = Environment.GetEnvironmentVariable("WMS_SQLSERVER_TEST_CONNECTION")!;
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(configured) { InitialCatalog = $"WmsIdempotencyConcurrency_{Guid.NewGuid():N}" };
        var options = new DbContextOptionsBuilder<WarehouseDbContext>().UseSqlServer(builder.ConnectionString).Options;
        await using var factory = new TestDbContextFactory(options);
        await using (var setup = await factory.CreateDbContextAsync()) await setup.Database.MigrateAsync();

        var attempts = await Task.WhenAll(
            RegisterAsync(new SqlServerBusinessWorkflowStore(factory), "same-hash"),
            RegisterAsync(new SqlServerBusinessWorkflowStore(factory), "same-hash"));

        Assert.Equal(1, attempts.Count(result => !result.Replayed));
        Assert.Equal(1, attempts.Count(result => result.Replayed));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SqlServerBusinessWorkflowStore(factory).RegisterIdempotencyAsync("scope", "key", "different-hash"));
    }

    [SqlServerFact]
    public async Task Sql_save_cancellation_is_propagated_before_opening_transaction()
    {
        var configured = Environment.GetEnvironmentVariable("WMS_SQLSERVER_TEST_CONNECTION")!;
        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(configured) { InitialCatalog = $"WmsCancellation_{Guid.NewGuid():N}" };
        var options = new DbContextOptionsBuilder<WarehouseDbContext>().UseSqlServer(builder.ConnectionString).Options;
        await using var factory = new TestDbContextFactory(options);
        await using (var setup = await factory.CreateDbContextAsync()) await setup.Database.MigrateAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SqlServerBusinessWorkflowStore(factory).SaveAsync(
                new BusinessWorkflowSnapshot("Relocation", "REL-CANCEL", 1, "Queued", "{}", DateTimeOffset.UtcNow), 0, cancellationToken: cancellation.Token));
    }

    private static async Task<bool> TrySaveAsync(SqlServerBusinessWorkflowStore store, BusinessWorkflowSnapshot snapshot)
    {
        try
        {
            await store.SaveAsync(snapshot, 1);
            return true;
        }
        catch (BusinessWorkflowConcurrencyException)
        {
            return false;
        }
    }

    private static Task<BusinessWorkflowIdempotencyResult> RegisterAsync(SqlServerBusinessWorkflowStore store, string hash)
        => store.RegisterIdempotencyAsync("scope", "key", hash);

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
