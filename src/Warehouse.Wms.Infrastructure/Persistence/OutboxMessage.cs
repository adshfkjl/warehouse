namespace Warehouse.Wms.Infrastructure.Persistence;

public enum OutboxMessageStatus
{
    Pending,
    Claimed,
    Published
}

/// <summary>
/// Durable message written in the same short transaction as the business
/// state change. Publishing happens later and never holds that transaction
/// open while waiting for a PLC or network response.
/// </summary>
public sealed class OutboxMessage
{
    private OutboxMessage() { }

    public OutboxMessage(
        string messageType,
        string aggregateType,
        string aggregateId,
        string idempotencyKey,
        string payload,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? nextAttemptAt = null)
    {
        Id = Guid.NewGuid();
        MessageType = Require(messageType, nameof(messageType));
        AggregateType = Require(aggregateType, nameof(aggregateType));
        AggregateId = Require(aggregateId, nameof(aggregateId));
        IdempotencyKey = Require(idempotencyKey, nameof(idempotencyKey));
        Payload = Require(payload, nameof(payload));
        CreatedAt = Normalize(createdAt ?? DateTimeOffset.UtcNow);
        NextAttemptAt = Normalize(nextAttemptAt ?? CreatedAt);
        Status = OutboxMessageStatus.Pending;
        Version = 1;
    }

    public Guid Id { get; private set; }

    public string MessageType { get; private set; } = null!;

    public string AggregateType { get; private set; } = null!;

    public string AggregateId { get; private set; } = null!;

    public string IdempotencyKey { get; private set; } = null!;

    public string Payload { get; private set; } = null!;

    public OutboxMessageStatus Status { get; private set; }

    public int AttemptCount { get; private set; }

    public int Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset NextAttemptAt { get; private set; }

    public DateTimeOffset? ClaimedAt { get; private set; }

    public DateTimeOffset? ClaimExpiresAt { get; private set; }

    public string? ClaimedBy { get; private set; }

    public DateTimeOffset? PublishedAt { get; private set; }

    public DateTimeOffset? LastAttemptAt { get; private set; }

    public string? LastError { get; private set; }

    public bool IsDispatchable(DateTimeOffset? at = null)
    {
        var timestamp = Normalize(at ?? DateTimeOffset.UtcNow);
        return Status != OutboxMessageStatus.Published
               && NextAttemptAt <= timestamp
               && (Status == OutboxMessageStatus.Pending
                   || (Status == OutboxMessageStatus.Claimed && ClaimExpiresAt <= timestamp));
    }

    public void Claim(string workerId, DateTimeOffset claimedAt, TimeSpan leaseDuration)
    {
        var worker = Require(workerId, nameof(workerId));
        var timestamp = Normalize(claimedAt);
        if (Status == OutboxMessageStatus.Published)
        {
            throw new InvalidOperationException("A published outbox message cannot be claimed.");
        }

        if (!IsDispatchable(timestamp))
        {
            throw new InvalidOperationException("The outbox message is not dispatchable at this time.");
        }

        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), leaseDuration, "Claim lease must be positive.");
        }

        Status = OutboxMessageStatus.Claimed;
        ClaimedBy = worker;
        ClaimedAt = timestamp;
        ClaimExpiresAt = timestamp.Add(leaseDuration);
        LastAttemptAt = timestamp;
        AttemptCount = checked(AttemptCount + 1);
        Version = checked(Version + 1);
    }

    public void MarkPublished(string workerId, DateTimeOffset publishedAt)
    {
        EnsureClaim(workerId, publishedAt);
        var timestamp = Normalize(publishedAt);
        Status = OutboxMessageStatus.Published;
        PublishedAt = timestamp;
        ClearClaim();
        LastError = null;
        Version = checked(Version + 1);
    }

    public void MarkFailed(
        string workerId,
        string error,
        DateTimeOffset failedAt,
        DateTimeOffset nextAttemptAt)
    {
        EnsureClaim(workerId, failedAt);
        LastError = Require(error, nameof(error));
        Status = OutboxMessageStatus.Pending;
        NextAttemptAt = Normalize(nextAttemptAt);
        ClearClaim();
        Version = checked(Version + 1);
    }

    private void EnsureClaim(string workerId, DateTimeOffset at)
    {
        var worker = Require(workerId, nameof(workerId));
        if (Status != OutboxMessageStatus.Claimed || !string.Equals(ClaimedBy, worker, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The outbox message is not claimed by this worker.");
        }

        if (ClaimExpiresAt <= Normalize(at))
        {
            throw new InvalidOperationException("The outbox message claim has expired.");
        }
    }

    private void ClearClaim()
    {
        ClaimedBy = null;
        ClaimedAt = null;
        ClaimExpiresAt = null;
    }

    private static string Require(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty value is required.", parameterName);
        }

        return value.Trim();
    }

    private static DateTimeOffset Normalize(DateTimeOffset value) => value.ToUniversalTime();
}
