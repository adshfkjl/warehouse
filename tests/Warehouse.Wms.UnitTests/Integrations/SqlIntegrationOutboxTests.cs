using Warehouse.Wms.Application.Integrations;
using Warehouse.Wms.Infrastructure.Integrations;
using Warehouse.Wms.Infrastructure.Persistence;

namespace Warehouse.Wms.UnitTests.Integrations;

public sealed class SqlIntegrationOutboxTests
{
    [Fact]
    public async Task Enqueue_maps_integration_message_to_durable_outbox_and_preserves_replay_semantics()
    {
        var store = new RecordingOutboxStore();
        var outbox = new SqlServerIntegrationOutbox(store);
        var message = new IntegrationMessage(
            IntegrationMessageType.InboundNotice,
            "ERP",
            "v1",
            "in-001",
            "corr-1",
            "{\"documentNumber\":\"IN-001\"}",
            "ABC123");

        var first = await outbox.EnqueueAsync(message);
        var replay = await outbox.EnqueueAsync(message);

        Assert.True(first.Accepted);
        Assert.False(first.Duplicate);
        Assert.Equal("Queued", first.Status);
        Assert.True(replay.Accepted);
        Assert.True(replay.Duplicate);
        Assert.Equal("AlreadyQueued", replay.Status);
        var persisted = Assert.Single(store.Messages);
        Assert.Equal("integration.InboundNotice", persisted.MessageType);
        Assert.Equal("ExternalIntegration", persisted.AggregateType);
        Assert.Equal(message.IdempotencyKey, persisted.AggregateId);
        Assert.Equal(message.IdempotencyKey, persisted.IdempotencyKey);
        Assert.Contains(message.PayloadSha256, persisted.Payload);
    }

    [Fact]
    public async Task Replay_with_a_different_payload_digest_is_rejected()
    {
        var store = new RecordingOutboxStore();
        var outbox = new SqlServerIntegrationOutbox(store);
        var first = new IntegrationMessage(IntegrationMessageType.StatusQuery, "ERP", "v1", "status-001", null, "{}", "ABC123");
        var conflicting = first with { Payload = "{\"changed\":true}", PayloadSha256 = "DEF456" };

        await outbox.EnqueueAsync(first);

        await Assert.ThrowsAsync<InvalidOperationException>(() => outbox.EnqueueAsync(conflicting));
    }

    private sealed class RecordingOutboxStore : IOutboxMessageStore
    {
        private readonly Dictionary<string, OutboxMessage> _messages = new(StringComparer.Ordinal);

        public IReadOnlyCollection<OutboxMessage> Messages => _messages.Values.ToArray();

        public Task<OutboxEnqueueResult> EnqueueAsync(OutboxMessage message, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_messages.TryGetValue(message.IdempotencyKey, out var existing))
            {
                return Task.FromResult(new OutboxEnqueueResult(existing, true));
            }

            _messages.Add(message.IdempotencyKey, message);
            return Task.FromResult(new OutboxEnqueueResult(message, false));
        }

        public Task<OutboxMessage?> ClaimNextAsync(string workerId, DateTimeOffset claimedAt, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task MarkPublishedAsync(Guid messageId, string workerId, DateTimeOffset publishedAt, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task MarkFailedAsync(Guid messageId, string workerId, string failure, DateTimeOffset failedAt, DateTimeOffset nextAttemptAt, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
