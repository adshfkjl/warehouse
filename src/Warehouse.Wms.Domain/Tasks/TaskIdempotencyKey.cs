namespace Warehouse.Wms.Domain.Tasks;

/// <summary>
/// Records the request fingerprint used to make a task command, device
/// submission, or result callback safe to replay.
/// </summary>
public sealed class TaskIdempotencyKey
{
    private TaskIdempotencyKey() { }

    public TaskIdempotencyKey(
        string scope,
        string key,
        string requestHash,
        Guid? taskId = null,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? expiresAt = null)
    {
        Scope = Require(scope, nameof(scope));
        Key = Require(key, nameof(key));
        RequestHash = Require(requestHash, nameof(requestHash));
        TaskId = taskId;
        CreatedAt = Normalize(createdAt ?? DateTimeOffset.UtcNow);

        if (expiresAt.HasValue && expiresAt.Value <= CreatedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAt), expiresAt, "Expiry must be after creation.");
        }

        Id = Guid.NewGuid();
        ExpiresAt = expiresAt.HasValue ? Normalize(expiresAt.Value) : null;
    }

    public Guid Id { get; private set; }

    /// <summary>Separates keys used by different command/result contracts.</summary>
    public string Scope { get; private set; } = null!;

    public string Key { get; private set; } = null!;

    /// <summary>Canonical request or message payload hash.</summary>
    public string RequestHash { get; private set; } = null!;

    public Guid? TaskId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? ExpiresAt { get; private set; }

    /// <summary>
    /// Value suitable for a database unique constraint. Scope is part of the
    /// key so the same opaque value can be used by separate contracts safely.
    /// </summary>
    public string UniqueKey => BuildUniqueKey(Scope, Key);

    public bool IsExpired(DateTimeOffset? at = null)
        => ExpiresAt.HasValue && ExpiresAt.Value <= Normalize(at ?? DateTimeOffset.UtcNow);

    public bool Matches(string scope, string key, string requestHash)
        => string.Equals(Scope, NormalizeRequired(scope, nameof(scope)), StringComparison.Ordinal)
           && string.Equals(Key, NormalizeRequired(key, nameof(key)), StringComparison.Ordinal)
           && string.Equals(RequestHash, NormalizeRequired(requestHash, nameof(requestHash)), StringComparison.Ordinal);

    public void EnsureRequestMatches(string scope, string key, string requestHash)
    {
        var normalizedScope = NormalizeRequired(scope, nameof(scope));
        var normalizedKey = NormalizeRequired(key, nameof(key));
        var normalizedHash = NormalizeRequired(requestHash, nameof(requestHash));

        if (!string.Equals(Scope, normalizedScope, StringComparison.Ordinal)
            || !string.Equals(Key, normalizedKey, StringComparison.Ordinal)
            || !string.Equals(RequestHash, normalizedHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Idempotency key '{UniqueKey}' was reused with a different request fingerprint.");
        }
    }

    public static string BuildUniqueKey(string scope, string key)
        => $"{NormalizeRequired(scope, nameof(scope))}:{NormalizeRequired(key, nameof(key))}";

    private static string Require(string value, string parameterName) => NormalizeRequired(value, parameterName);

    private static string NormalizeRequired(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty value is required.", parameterName);
        }

        return value.Trim();
    }

    private static DateTimeOffset Normalize(DateTimeOffset value) => value.ToUniversalTime();
}
