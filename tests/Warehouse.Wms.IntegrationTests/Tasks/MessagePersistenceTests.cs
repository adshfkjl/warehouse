using Microsoft.EntityFrameworkCore;
using Warehouse.Wms.Infrastructure.Persistence;

namespace Warehouse.Wms.IntegrationTests.Tasks;

public sealed class MessagePersistenceTests
{
    [SqlServerFact]
    public async Task Outbox_is_idempotent_and_recovers_expired_claim()
    {
        var connectionString = Environment.GetEnvironmentVariable("WMS_SQLSERVER_TEST_CONNECTION");
        Assert.False(string.IsNullOrWhiteSpace(connectionString));
        await using var factory = CreateFactory(connectionString!);
        await using (var setup = await factory.CreateDbContextAsync())
        {
            await setup.Database.MigrateAsync();
            await setup.Database.ExecuteSqlRawAsync("DELETE FROM OutboxMessages; DELETE FROM InboxMessages;");
        }

        var store = new SqlServerMessageStore(factory);
        var created = new DateTimeOffset(2026, 8, 25, 4, 0, 0, TimeSpan.Zero);
        var message = new OutboxMessage("DeviceCommand", "WarehouseTask", "TASK-MSG-001", "cmd-msg-001", "{\"task\":1}", created);
        var first = await store.EnqueueOutboxAsync(message);
        var replay = await store.EnqueueOutboxAsync(new OutboxMessage("DeviceCommand", "WarehouseTask", "TASK-MSG-001", "cmd-msg-001", "{\"task\":1}", created));

        Assert.False(first.Replayed);
        Assert.True(replay.Replayed);
        var claimed = await store.ClaimOutboxAsync("worker-1", created, TimeSpan.FromMinutes(1));
        Assert.NotNull(claimed);
        Assert.Equal(message.Id, claimed.Id);
        Assert.Null(await store.ClaimOutboxAsync("worker-2", created.AddSeconds(10), TimeSpan.FromMinutes(1)));

        var recovered = await store.ClaimOutboxAsync("worker-2", created.AddMinutes(2), TimeSpan.FromMinutes(1));
        Assert.NotNull(recovered);
        Assert.Equal(2, recovered.AttemptCount);
        await store.MarkOutboxPublishedAsync(recovered.Id, "worker-2", created.AddMinutes(2).AddSeconds(1));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.EnqueueOutboxAsync(new OutboxMessage("DeviceCommand", "WarehouseTask", "TASK-MSG-001", "cmd-msg-001", "{\"task\":2}", created)));
    }

    [SqlServerFact]
    public async Task Inbox_deduplicates_old_results_and_processes_newer_result_once()
    {
        var connectionString = Environment.GetEnvironmentVariable("WMS_SQLSERVER_TEST_CONNECTION");
        Assert.False(string.IsNullOrWhiteSpace(connectionString));
        await using var factory = CreateFactory(connectionString!);
        await using (var setup = await factory.CreateDbContextAsync())
        {
            await setup.Database.MigrateAsync();
            await setup.Database.ExecuteSqlRawAsync("DELETE FROM OutboxMessages; DELETE FROM InboxMessages;");
        }

        var store = new SqlServerMessageStore(factory);
        var received = new DateTimeOffset(2026, 8, 25, 5, 0, 0, TimeSpan.Zero);
        var first = new InboxMessage("result-msg-001", "DeviceObservation", "cmd-msg-002", "{\"status\":\"Executing\"}", received, 1, "Polling");
        var accepted = await store.EnqueueInboxAsync(first);
        var duplicate = await store.EnqueueInboxAsync(new InboxMessage("result-msg-001", "DeviceObservation", "cmd-msg-002", "{\"status\":\"Executing\"}", received, 1, "Polling"));
        var old = await store.EnqueueInboxAsync(new InboxMessage("result-msg-002", "DeviceObservation", "cmd-msg-002", "{\"status\":\"Accepted\"}", received.AddSeconds(1), 0, "Callback"));
        var newer = await store.EnqueueInboxAsync(new InboxMessage("result-msg-003", "DeviceObservation", "cmd-msg-002", "{\"status\":\"Succeeded\"}", received.AddSeconds(2), 2, "Polling"));

        Assert.False(accepted.Replayed);
        Assert.True(duplicate.Replayed);
        Assert.True(old.Replayed);
        Assert.True(newer.Replayed is false);
        Assert.Equal(first.Id, duplicate.Message.Id);
        Assert.Equal(first.Id, old.Message.Id);
        Assert.NotEqual(first.Id, newer.Message.Id);

        var claimed = await store.ClaimInboxAsync("processor-1", received, TimeSpan.FromMinutes(1));
        Assert.NotNull(claimed);
        await store.MarkInboxProcessedAsync(claimed.Id, "processor-1", received.AddSeconds(3));
        var newerClaim = await store.ClaimInboxAsync("processor-2", received.AddSeconds(4), TimeSpan.FromMinutes(1));
        Assert.NotNull(newerClaim);
        Assert.Equal(newer.Message.Id, newerClaim.Id);
        await store.MarkInboxFailedAsync(newerClaim.Id, "processor-2", "temporary", received.AddSeconds(5));
        var retry = await store.ClaimInboxAsync("processor-3", received.AddSeconds(6), TimeSpan.FromMinutes(1));
        Assert.NotNull(retry);
        Assert.Equal(newer.Message.Id, retry.Id);
    }

    [SqlServerFact]
    public async Task Message_version_conflict_is_rejected_without_overwriting_claim()
    {
        var connectionString = Environment.GetEnvironmentVariable("WMS_SQLSERVER_TEST_CONNECTION");
        Assert.False(string.IsNullOrWhiteSpace(connectionString));
        await using var factory = CreateFactory(connectionString!);
        await using (var setup = await factory.CreateDbContextAsync())
        {
            await setup.Database.MigrateAsync();
            await setup.Database.ExecuteSqlRawAsync("DELETE FROM OutboxMessages; DELETE FROM InboxMessages;");
        }

        var store = new SqlServerMessageStore(factory);
        var at = DateTimeOffset.UtcNow;
        var message = new OutboxMessage("DeviceCommand", "WarehouseTask", "TASK-MSG-003", "cmd-msg-003", "{}", at);
        await store.EnqueueOutboxAsync(message);
        var claimed = await store.ClaimOutboxAsync("worker-1", at, TimeSpan.FromMinutes(5));
        Assert.NotNull(claimed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.MarkOutboxPublishedAsync(claimed.Id, "worker-2", at.AddSeconds(1)));
        await store.MarkOutboxFailedAsync(claimed.Id, "worker-1", "retry", at.AddSeconds(1), at.AddMinutes(1));
    }

    private static TestDbContextFactory CreateFactory(string connectionString)
        => new(new DbContextOptionsBuilder<WarehouseDbContext>().UseSqlServer(connectionString).Options);

    private sealed class SqlServerFactAttribute : FactAttribute
    {
        public SqlServerFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WMS_SQLSERVER_TEST_CONNECTION")))
            {
                Skip = "Set WMS_SQLSERVER_TEST_CONNECTION to run the SQL Server message persistence fixture.";
            }
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
}
