using Warehouse.Wms.Application.Tasks;
using Warehouse.Wms.Domain.Tasks;

namespace Warehouse.Wms.UnitTests.Tasks;

public sealed class TaskPersistenceContractTests
{
    [Fact]
    public async Task In_memory_store_round_trips_task_and_state_history()
    {
        var store = new InMemoryTaskPersistenceStore();
        var task = new WarehouseTask("TASK-PERSIST-001", "Putaway", new DateTimeOffset(2026, 8, 26, 1, 0, 0, TimeSpan.Zero));

        await store.CreateTaskAsync(task);
        await store.TransitionTaskAsync("TASK-PERSIST-001", 1, TaskState.Allocated, "operator", "allocated");
        var restored = await store.GetTaskAsync("TASK-PERSIST-001");

        Assert.NotNull(restored);
        Assert.Equal(TaskState.Allocated, restored!.State);
        Assert.Equal(2, restored.Version);
        Assert.Single(restored.StateHistory);
    }

    [Fact]
    public async Task Idempotency_registration_replays_same_hash_and_rejects_conflict()
    {
        var store = new InMemoryTaskPersistenceStore();
        var key = new TaskIdempotencyKey("device-command", "cmd-001", "hash-a");

        var first = await store.RegisterIdempotencyKeyAsync(key);
        var replay = await store.RegisterIdempotencyKeyAsync(new TaskIdempotencyKey("device-command", "cmd-001", "hash-a"));

        Assert.False(first.Replayed);
        Assert.True(replay.Replayed);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.RegisterIdempotencyKeyAsync(new TaskIdempotencyKey("device-command", "cmd-001", "hash-b")));
    }

    [Fact]
    public async Task Resource_lock_requires_current_version_and_allows_expired_replacement()
    {
        var store = new InMemoryTaskPersistenceStore();
        var acquired = new DateTimeOffset(2026, 8, 26, 1, 0, 0, TimeSpan.Zero);
        var first = await store.AcquireResourceLockAsync("Location", "L-001", "TASK-001", acquired, TimeSpan.FromMinutes(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AcquireResourceLockAsync(
            "Location", "L-001", "TASK-002", acquired.AddSeconds(30), TimeSpan.FromMinutes(1)));

        var replacement = await store.AcquireResourceLockAsync("Location", "L-001", "TASK-002", acquired.AddMinutes(2), TimeSpan.FromMinutes(1));
        Assert.Equal("TASK-002", replacement.OwnerTaskNumber);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RenewResourceLockAsync(
            first.Id, "TASK-001", 1, acquired.AddMinutes(2), TimeSpan.FromMinutes(1), first.LockToken));
    }
}
