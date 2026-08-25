using Warehouse.Wms.Application.Tasks;

namespace Warehouse.Wms.UnitTests.Tasks;

public sealed class BusinessWorkflowStoreTests
{
    [Fact]
    public async Task InMemory_store_round_trips_snapshot_and_history_after_new_instance()
    {
        var store = new InMemoryBusinessWorkflowStore();
        var created = new BusinessWorkflowSnapshot(
            "InboundOrder", "IN-001", 1, "Received", "{\"orderNumber\":\"IN-001\"}",
            DateTimeOffset.UtcNow, "TASK-001");

        await store.SaveAsync(created, expectedVersion: 0);
        await store.SaveAsync(created with { Version = 2, Status = "PutawayQueued" }, expectedVersion: 1);

        var restarted = store.CreateRestartedInstance();
        var restored = await restarted.GetAsync("InboundOrder", "IN-001");
        var history = await restarted.GetHistoryAsync("InboundOrder", "IN-001");

        Assert.Equal(2, restored!.Version);
        Assert.Equal("PutawayQueued", restored.Status);
        Assert.Single(history);
        Assert.Equal("Received", history[0].FromStatus);
    }

    [Fact]
    public async Task InMemory_store_rejects_version_conflict_and_replays_idempotency()
    {
        var store = new InMemoryBusinessWorkflowStore();
        var snapshot = new BusinessWorkflowSnapshot(
            "OutboundTask", "OUT-001", 1, "AwaitingReview", "{}", DateTimeOffset.UtcNow, "TASK-OUT-001");

        await store.SaveAsync(snapshot, expectedVersion: 0);
        await Assert.ThrowsAsync<BusinessWorkflowConcurrencyException>(() =>
            store.SaveAsync(snapshot with { Version = 2 }, expectedVersion: 0));

        var first = await store.RegisterIdempotencyAsync("outbound-review", "review-001", "hash-1", "OutboundTask", "OUT-001");
        var replay = await store.RegisterIdempotencyAsync("outbound-review", "review-001", "hash-1", "OutboundTask", "OUT-001");
        Assert.False(first.Replayed);
        Assert.True(replay.Replayed);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.RegisterIdempotencyAsync("outbound-review", "review-001", "different", "OutboundTask", "OUT-001"));
    }
}
