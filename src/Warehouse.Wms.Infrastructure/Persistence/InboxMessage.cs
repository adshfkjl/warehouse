namespace Warehouse.Wms.Infrastructure.Persistence;

public enum InboxMessageStatus
{
    Pending,
    Claimed,
    Processed
}

/// <summary>
/// Durable receipt for a device result. Message identity and result version
/// are retained so polling and callback deliveries can share one dedup path.
/// </summary>
public sealed class InboxMessage
{
    private InboxMessage() { }

    public InboxMessage(
        string messageId,
        string messageType,
        string idempotencyKey,
        string payload,
        DateTimeOffset? receivedAt = null,
        long resultVersion = 0,
        string? source = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(resultVersion);
        Id = Guid.NewGuid();
        MessageId = Require(messageId, nameof(messageId));
        MessageType = Require(messageType, nameof(messageType));
        IdempotencyKey = Require(idempotencyKey, nameof(idempotencyKey));
        Payload = Require(payload, nameof(payload));
        ReceivedAt = Normalize(receivedAt ?? DateTimeOffset.UtcNow);
        ResultVersion = resultVersion;
        Source = string.IsNullOrWhiteSpace(source) ? null : source.Trim();
        Status = InboxMessageStatus.Pending;
        Version = 1;
    }

    public Guid Id { get; private set; }

    public string MessageId { get; private set; } = null!;

    public string MessageType { get; private set; } = null!;

    public string IdempotencyKey { get; private set; } = null!;

    public string Payload { get; private set; } = null!;

    public string? Source { get; private set; }

    public long ResultVersion { get; private set; }

    public InboxMessageStatus Status { get; private set; }

    public int Version { get; private set; }

    public DateTimeOffset ReceivedAt { get; private set; }

    public DateTimeOffset? ClaimedAt { get; private set; }

    public DateTimeOffset? ClaimExpiresAt { get; private set; }

    public string? ClaimedBy { get; private set; }

    public DateTimeOffset? ProcessedAt { get; private set; }

    public string? LastError { get; private set; }

    public bool Matches(string messageId, string idempotencyKey, long resultVersion)
        => string.Equals(MessageId, Require(messageId, nameof(messageId)), StringComparison.Ordinal)
           && string.Equals(IdempotencyKey, Require(idempotencyKey, nameof(idempotencyKey)), StringComparison.Ordinal)
           && ResultVersion == resultVersion;

    public bool IsDuplicateOf(string messageId, string idempotencyKey, long resultVersion)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(resultVersion);
        var sameMessage = string.Equals(MessageId, Require(messageId, nameof(messageId)), StringComparison.Ordinal);
        var sameCommand = string.Equals(IdempotencyKey, Require(idempotencyKey, nameof(idempotencyKey)), StringComparison.Ordinal);
        return sameMessage || (sameCommand && resultVersion <= ResultVersion);
    }

    public void Claim(string workerId, DateTimeOffset claimedAt, TimeSpan leaseDuration)
    {
        var worker = Require(workerId, nameof(workerId));
        var timestamp = Normalize(claimedAt);
        if (Status == InboxMessageStatus.Processed)
        {
            throw new InvalidOperationException("A processed inbox message cannot be claimed.");
        }

        if (Status == InboxMessageStatus.Claimed && ClaimExpiresAt > timestamp)
        {
            throw new InvalidOperationException("The inbox message is already claimed.");
        }

        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), leaseDuration, "Claim lease must be positive.");
        }

        Status = InboxMessageStatus.Claimed;
        ClaimedBy = worker;
        ClaimedAt = timestamp;
        ClaimExpiresAt = timestamp.Add(leaseDuration);
        Version = checked(Version + 1);
    }

    public void MarkProcessed(string workerId, DateTimeOffset processedAt)
    {
        var worker = Require(workerId, nameof(workerId));
        if (Status == InboxMessageStatus.Processed)
        {
            throw new InvalidOperationException("The inbox message has already been processed.");
        }

        if (Status != InboxMessageStatus.Claimed || !string.Equals(ClaimedBy, worker, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The inbox message must be claimed by this worker before processing.");
        }

        if (ClaimExpiresAt <= Normalize(processedAt))
        {
            throw new InvalidOperationException("The inbox message claim has expired.");
        }

        Status = InboxMessageStatus.Processed;
        ProcessedAt = Normalize(processedAt);
        LastError = null;
        ClaimedBy = null;
        ClaimedAt = null;
        ClaimExpiresAt = null;
        Version = checked(Version + 1);
    }

    public void MarkFailed(string workerId, string error, DateTimeOffset failedAt)
    {
        var worker = Require(workerId, nameof(workerId));
        if (Status != InboxMessageStatus.Claimed || !string.Equals(ClaimedBy, worker, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The inbox message is not claimed by this worker.");
        }

        LastError = Require(error, nameof(error));
        Status = InboxMessageStatus.Pending;
        ClaimedBy = null;
        ClaimedAt = null;
        ClaimExpiresAt = null;
        Version = checked(Version + 1);
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
