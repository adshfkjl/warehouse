using System.Data;
using Microsoft.EntityFrameworkCore;

namespace Warehouse.Wms.Infrastructure.Persistence;

public sealed record OutboxEnqueueResult(OutboxMessage Message, bool Replayed);

public sealed record InboxEnqueueResult(InboxMessage Message, bool Replayed);

public interface IOutboxMessageStore
{
    Task<OutboxEnqueueResult> EnqueueAsync(OutboxMessage message, CancellationToken cancellationToken = default);

    Task<OutboxMessage?> ClaimNextAsync(
        string workerId,
        DateTimeOffset claimedAt,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    Task MarkPublishedAsync(
        Guid messageId,
        string workerId,
        DateTimeOffset publishedAt,
        CancellationToken cancellationToken = default);

    Task MarkFailedAsync(
        Guid messageId,
        string workerId,
        string failure,
        DateTimeOffset failedAt,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken = default);
}

public interface IInboxMessageStore
{
    Task<InboxEnqueueResult> EnqueueAsync(InboxMessage message, CancellationToken cancellationToken = default);

    Task<InboxMessage?> ClaimNextInboxAsync(
        string workerId,
        DateTimeOffset claimedAt,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    Task MarkProcessedAsync(
        Guid messageId,
        string workerId,
        DateTimeOffset processedAt,
        CancellationToken cancellationToken = default);

    Task MarkFailedAsync(
        Guid messageId,
        string workerId,
        string failure,
        DateTimeOffset failedAt,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// SQL Server persistence boundary for Outbox and Inbox messages. Every
/// operation owns a short transaction; no caller should hold it while waiting
/// for a device, network response or callback.
/// </summary>
public sealed class SqlServerMessageStore(IDbContextFactory<WarehouseDbContext> dbContextFactory)
    : IOutboxMessageStore, IInboxMessageStore
{
    public async Task<OutboxEnqueueResult> EnqueueAsync(
        OutboxMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        var existing = await db.OutboxMessages
            .SingleOrDefaultAsync(x => x.IdempotencyKey == message.IdempotencyKey, cancellationToken);
        if (existing is not null)
        {
            EnsureEquivalent(existing, message);
            await transaction.CommitAsync(cancellationToken);
            return new OutboxEnqueueResult(existing, true);
        }

        db.OutboxMessages.Add(message);
        await SaveAndCommitAsync(db, transaction, cancellationToken);
        return new OutboxEnqueueResult(message, false);
    }

    public async Task<InboxEnqueueResult> EnqueueAsync(
        InboxMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        var byMessageId = await db.InboxMessages
            .SingleOrDefaultAsync(x => x.MessageId == message.MessageId, cancellationToken);
        if (byMessageId is not null)
        {
            EnsureEquivalent(byMessageId, message);
            await transaction.CommitAsync(cancellationToken);
            return new InboxEnqueueResult(byMessageId, true);
        }

        var sameVersion = await db.InboxMessages
            .SingleOrDefaultAsync(
                x => x.IdempotencyKey == message.IdempotencyKey && x.ResultVersion == message.ResultVersion,
                cancellationToken);
        if (sameVersion is not null)
        {
            EnsureEquivalent(sameVersion, message);
            await transaction.CommitAsync(cancellationToken);
            return new InboxEnqueueResult(sameVersion, true);
        }

        var latest = await db.InboxMessages
            .Where(x => x.IdempotencyKey == message.IdempotencyKey)
            .OrderByDescending(x => x.ResultVersion)
            .ThenByDescending(x => x.ReceivedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (latest is not null && message.ResultVersion < latest.ResultVersion)
        {
            await transaction.CommitAsync(cancellationToken);
            return new InboxEnqueueResult(latest, true);
        }

        db.InboxMessages.Add(message);
        await SaveAndCommitAsync(db, transaction, cancellationToken);
        return new InboxEnqueueResult(message, false);
    }

    public Task<OutboxEnqueueResult> EnqueueOutboxAsync(
        OutboxMessage message,
        CancellationToken cancellationToken = default)
        => EnqueueAsync(message, cancellationToken);

    public Task<InboxEnqueueResult> EnqueueInboxAsync(
        InboxMessage message,
        CancellationToken cancellationToken = default)
        => EnqueueAsync(message, cancellationToken);

    public async Task<OutboxMessage?> ClaimNextAsync(
        string workerId,
        DateTimeOffset claimedAt,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var timestamp = claimedAt.ToUniversalTime();
        var entity = await db.OutboxMessages
            .Where(x => x.Status != OutboxMessageStatus.Published
                        && x.NextAttemptAt <= timestamp
                        && (x.Status == OutboxMessageStatus.Pending
                            || (x.Status == OutboxMessageStatus.Claimed && x.ClaimExpiresAt <= timestamp)))
            .OrderBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (entity is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        entity.Claim(workerId, timestamp, leaseDuration);
        await SaveAndCommitAsync(db, transaction, cancellationToken);
        return entity;
    }

    public Task<OutboxMessage?> ClaimOutboxAsync(
        string workerId,
        DateTimeOffset claimedAt,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
        => ClaimNextAsync(workerId, claimedAt, leaseDuration, cancellationToken);

    public async Task<InboxMessage?> ClaimNextInboxAsync(
        string workerId,
        DateTimeOffset claimedAt,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var timestamp = claimedAt.ToUniversalTime();
        var entity = await db.InboxMessages
            .Where(x => x.Status != InboxMessageStatus.Processed
                        && (x.Status == InboxMessageStatus.Pending
                            || (x.Status == InboxMessageStatus.Claimed && x.ClaimExpiresAt <= timestamp)))
            .OrderBy(x => x.ReceivedAt)
            .ThenBy(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (entity is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        entity.Claim(workerId, timestamp, leaseDuration);
        await SaveAndCommitAsync(db, transaction, cancellationToken);
        return entity;
    }

    public Task<InboxMessage?> ClaimInboxAsync(
        string workerId,
        DateTimeOffset claimedAt,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
        => ClaimNextInboxAsync(workerId, claimedAt, leaseDuration, cancellationToken);

    public async Task MarkPublishedAsync(
        Guid messageId,
        string workerId,
        DateTimeOffset publishedAt,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var entity = await db.OutboxMessages.SingleOrDefaultAsync(x => x.Id == messageId, cancellationToken)
            ?? throw new KeyNotFoundException($"Outbox message '{messageId}' was not found.");
        entity.MarkPublished(workerId, publishedAt);
        await SaveAndCommitAsync(db, transaction, cancellationToken);
    }

    public Task MarkOutboxPublishedAsync(
        Guid messageId,
        string workerId,
        DateTimeOffset publishedAt,
        CancellationToken cancellationToken = default)
        => MarkPublishedAsync(messageId, workerId, publishedAt, cancellationToken);

    public async Task MarkFailedAsync(
        Guid messageId,
        string workerId,
        string failure,
        DateTimeOffset failedAt,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var entity = await db.OutboxMessages.SingleOrDefaultAsync(x => x.Id == messageId, cancellationToken)
            ?? throw new KeyNotFoundException($"Outbox message '{messageId}' was not found.");
        entity.MarkFailed(workerId, failure, failedAt, nextAttemptAt);
        await SaveAndCommitAsync(db, transaction, cancellationToken);
    }

    public Task MarkOutboxFailedAsync(
        Guid messageId,
        string workerId,
        string failure,
        DateTimeOffset failedAt,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken = default)
    {
        return this.MarkFailedAsync(messageId, workerId, failure, failedAt, nextAttemptAt, cancellationToken);
    }

    public async Task MarkProcessedAsync(
        Guid messageId,
        string workerId,
        DateTimeOffset processedAt,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var entity = await db.InboxMessages.SingleOrDefaultAsync(x => x.Id == messageId, cancellationToken)
            ?? throw new KeyNotFoundException($"Inbox message '{messageId}' was not found.");
        entity.MarkProcessed(workerId, processedAt);
        await SaveAndCommitAsync(db, transaction, cancellationToken);
    }

    public Task MarkInboxProcessedAsync(
        Guid messageId,
        string workerId,
        DateTimeOffset processedAt,
        CancellationToken cancellationToken = default)
        => MarkProcessedAsync(messageId, workerId, processedAt, cancellationToken);

    public async Task MarkInboxFailedAsync(
        Guid messageId,
        string workerId,
        string failure,
        DateTimeOffset failedAt,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var entity = await db.InboxMessages.SingleOrDefaultAsync(x => x.Id == messageId, cancellationToken)
            ?? throw new KeyNotFoundException($"Inbox message '{messageId}' was not found.");
        entity.MarkFailed(workerId, failure, failedAt);
        await SaveAndCommitAsync(db, transaction, cancellationToken);
    }

    public Task MarkFailedAsync(
        Guid messageId,
        string workerId,
        string failure,
        DateTimeOffset failedAt,
        CancellationToken cancellationToken = default)
        => MarkInboxFailedAsync(messageId, workerId, failure, failedAt, cancellationToken);

    private static async Task SaveAndCommitAsync(
        WarehouseDbContext db,
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new InvalidOperationException("The message changed concurrently; no message state was committed.", exception);
        }
    }

    private static void EnsureEquivalent(OutboxMessage existing, OutboxMessage incoming)
    {
        if (!string.Equals(existing.MessageType, incoming.MessageType, StringComparison.Ordinal)
            || !string.Equals(existing.AggregateType, incoming.AggregateType, StringComparison.Ordinal)
            || !string.Equals(existing.AggregateId, incoming.AggregateId, StringComparison.Ordinal)
            || !string.Equals(existing.Payload, incoming.Payload, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The outbox idempotency key was already used for a different message.");
        }
    }

    private static void EnsureEquivalent(InboxMessage existing, InboxMessage incoming)
    {
        if (!string.Equals(existing.MessageType, incoming.MessageType, StringComparison.Ordinal)
            || !string.Equals(existing.IdempotencyKey, incoming.IdempotencyKey, StringComparison.Ordinal)
            || !string.Equals(existing.Payload, incoming.Payload, StringComparison.Ordinal)
            || !string.Equals(existing.Source, incoming.Source, StringComparison.Ordinal)
            || existing.ResultVersion != incoming.ResultVersion)
        {
            throw new InvalidOperationException("The inbox message identity was already used for a different message.");
        }
    }
}
