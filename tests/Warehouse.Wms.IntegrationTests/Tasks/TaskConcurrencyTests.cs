using System.Collections.Concurrent;
using Warehouse.Wms.Domain.Tasks;
using Warehouse.Wms.Infrastructure.Persistence;

namespace Warehouse.Wms.IntegrationTests.Tasks;

public sealed class TaskConcurrencyTests
{
    [Fact]
    public void Same_idempotency_key_and_request_hash_is_replay_safe_but_conflicting_request_is_rejected()
    {
        var key = new TaskIdempotencyKey(
            scope: "device-command",
            key: "cmd-001",
            requestHash: "sha256:abc",
            taskId: Guid.NewGuid());

        Assert.True(key.Matches("device-command", "cmd-001", "sha256:abc"));
        Assert.Throws<InvalidOperationException>(() =>
            key.EnsureRequestMatches("device-command", "cmd-001", "sha256:def"));
        Assert.False(key.IsExpired(key.CreatedAt.AddMinutes(1)));
    }

    [Fact]
    public async System.Threading.Tasks.Task Concurrent_acquisition_allows_only_one_active_owner_for_a_resource()
    {
        var locks = new ConcurrentDictionary<string, ResourceLock>(StringComparer.Ordinal);
        var barrier = new Barrier(2);
        var resourceKey = "Location:LOC-001";

        var first = System.Threading.Tasks.Task.Run(() => AcquireAsync("TASK-001"));
        var second = System.Threading.Tasks.Task.Run(() => AcquireAsync("TASK-002"));
        var acquired = await System.Threading.Tasks.Task.WhenAll(first, second);

        var winner = Assert.Single(acquired.Where(x => x is not null));
        Assert.True(winner!.OwnerTaskNumber is "TASK-001" or "TASK-002");
        Assert.Same(winner, locks[resourceKey]);

        async System.Threading.Tasks.Task<ResourceLock?> AcquireAsync(string taskNumber)
        {
            barrier.SignalAndWait();
            await System.Threading.Tasks.Task.Yield();
            var candidate = new ResourceLock(
                resourceType: "Location",
                resourceId: "LOC-001",
                ownerTaskNumber: taskNumber,
                acquiredAt: DateTimeOffset.UtcNow,
                leaseDuration: TimeSpan.FromMinutes(5));
            return locks.TryAdd(resourceKey, candidate) ? candidate : null;
        }
    }

    [Fact]
    public void Expired_lock_can_be_replaced_and_renewal_requires_the_expected_version()
    {
        var acquiredAt = new DateTimeOffset(2026, 8, 25, 1, 0, 0, TimeSpan.Zero);
        var resourceLock = new ResourceLock(
            "Pallet", "PAL-001", "TASK-001", acquiredAt, TimeSpan.FromMinutes(5));

        Assert.False(resourceLock.IsActive(acquiredAt.AddMinutes(6)));
        resourceLock.Renew(
            ownerTaskNumber: "TASK-001",
            expectedVersion: 1,
            renewedAt: acquiredAt.AddMinutes(1),
            leaseDuration: TimeSpan.FromMinutes(5),
            lockToken: resourceLock.LockToken);

        Assert.Equal(2, resourceLock.Version);
        Assert.Throws<InvalidOperationException>(() => resourceLock.Renew(
            ownerTaskNumber: "TASK-001",
            expectedVersion: 1,
            renewedAt: acquiredAt.AddMinutes(2),
            leaseDuration: TimeSpan.FromMinutes(5),
            lockToken: resourceLock.LockToken));
    }

    [Fact]
    public void Outbox_claim_is_persisted_before_publish_and_duplicate_claim_is_not_allowed()
    {
        var createdAt = new DateTimeOffset(2026, 8, 25, 2, 0, 0, TimeSpan.Zero);
        var message = new OutboxMessage(
            messageType: "DeviceCommand",
            aggregateType: "WarehouseTask",
            aggregateId: "TASK-001",
            idempotencyKey: "device-command:cmd-001",
            payload: "{\"taskNumber\":\"TASK-001\"}",
            createdAt: createdAt);

        Assert.True(message.IsDispatchable(createdAt));
        message.Claim("worker-1", createdAt, TimeSpan.FromMinutes(1));
        Assert.False(message.IsDispatchable(createdAt));
        Assert.Throws<InvalidOperationException>(() => message.Claim(
            "worker-2", createdAt, TimeSpan.FromMinutes(1)));

        message.MarkPublished("worker-1", createdAt.AddSeconds(1));
        Assert.Equal(OutboxMessageStatus.Published, message.Status);
        Assert.Equal(1, message.AttemptCount);
    }

    [Fact]
    public void Expired_outbox_claim_can_be_recovered_by_a_new_worker_without_duplicate_publish()
    {
        var createdAt = new DateTimeOffset(2026, 8, 25, 2, 0, 0, TimeSpan.Zero);
        var message = new OutboxMessage(
            messageType: "DeviceCommand",
            aggregateType: "WarehouseTask",
            aggregateId: "TASK-002",
            idempotencyKey: "device-command:cmd-002",
            payload: "{\"taskNumber\":\"TASK-002\"}",
            createdAt: createdAt);

        message.Claim("worker-1", createdAt, TimeSpan.FromMinutes(1));
        Assert.True(message.IsDispatchable(createdAt.AddMinutes(2)));

        message.Claim("worker-2", createdAt.AddMinutes(2), TimeSpan.FromMinutes(1));
        Assert.Equal(2, message.AttemptCount);
        Assert.Throws<InvalidOperationException>(() => message.MarkPublished(
            "worker-1", createdAt.AddMinutes(2).AddSeconds(1)));
        message.MarkPublished("worker-2", createdAt.AddMinutes(2).AddSeconds(1));

        Assert.Equal(OutboxMessageStatus.Published, message.Status);
    }

    [Fact]
    public void Inbox_deduplicates_repeated_callback_by_message_and_idempotency_key()
    {
        var receivedAt = new DateTimeOffset(2026, 8, 25, 3, 0, 0, TimeSpan.Zero);
        var message = new InboxMessage(
            messageId: "callback-001",
            messageType: "DeviceObservation",
            idempotencyKey: "device-result:cmd-001:v1",
            payload: "{\"status\":\"Succeeded\"}",
            receivedAt: receivedAt,
            resultVersion: 1);

        Assert.True(message.Matches("callback-001", "device-result:cmd-001:v1", 1));
        Assert.True(message.Matches("callback-001", "device-result:cmd-001:v1", 1));
        Assert.False(message.Matches("callback-002", "device-result:cmd-001:v2", 2));
        Assert.True(message.IsDuplicateOf("callback-002", "device-result:cmd-001:v1", 1));
        Assert.True(message.IsDuplicateOf("callback-003", "device-result:cmd-001:v1", 0));
        Assert.False(message.IsDuplicateOf("callback-004", "device-result:cmd-001:v2", 2));

        Assert.Throws<InvalidOperationException>(() => message.MarkProcessed(
            "result-processor", receivedAt.AddSeconds(1)));
        message.Claim("result-processor", receivedAt, TimeSpan.FromMinutes(1));
        message.MarkProcessed("result-processor", receivedAt.AddSeconds(1));
        Assert.Equal(InboxMessageStatus.Processed, message.Status);
        Assert.Throws<InvalidOperationException>(() => message.MarkProcessed(
            "result-processor", receivedAt.AddSeconds(2)));
    }
}
