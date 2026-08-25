namespace Warehouse.Wms.Domain.Tasks;

/// <summary>
/// A lease on a physical WMS resource. The version is checked by every
/// renewal/release so stale workers cannot mutate a newer lease.
/// </summary>
public sealed class ResourceLock
{
    private ResourceLock() { }

    public ResourceLock(
        string resourceType,
        string resourceId,
        string ownerTaskNumber,
        DateTimeOffset acquiredAt,
        TimeSpan leaseDuration,
        Guid? lockToken = null)
    {
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), leaseDuration, "Lease duration must be positive.");
        }

        Id = Guid.NewGuid();
        ResourceType = Require(resourceType, nameof(resourceType));
        ResourceId = Require(resourceId, nameof(resourceId));
        OwnerTaskNumber = Require(ownerTaskNumber, nameof(ownerTaskNumber));
        LockToken = lockToken.GetValueOrDefault(Guid.NewGuid());
        if (LockToken == Guid.Empty)
        {
            throw new ArgumentException("A non-empty lock token is required.", nameof(lockToken));
        }

        AcquiredAt = Normalize(acquiredAt);
        ExpiresAt = AcquiredAt.Add(leaseDuration);
        Version = 1;
    }

    public Guid Id { get; private set; }

    public string ResourceType { get; private set; } = null!;

    public string ResourceId { get; private set; } = null!;

    public string OwnerTaskNumber { get; private set; } = null!;

    public Guid LockToken { get; private set; }

    public DateTimeOffset AcquiredAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? ReleasedAt { get; private set; }

    public int Version { get; private set; }

    public string ResourceKey => BuildResourceKey(ResourceType, ResourceId);

    public bool IsExpired(DateTimeOffset? at = null)
        => ExpiresAt <= Normalize(at ?? DateTimeOffset.UtcNow);

    public bool IsActive(DateTimeOffset? at = null)
        => ReleasedAt is null && !IsExpired(at);

    public bool BelongsTo(string ownerTaskNumber, Guid lockToken)
        => string.Equals(OwnerTaskNumber, Require(ownerTaskNumber, nameof(ownerTaskNumber)), StringComparison.Ordinal)
           && LockToken == lockToken;

    public static bool CanAcquire(IEnumerable<ResourceLock> existingLocks, string resourceType, string resourceId, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(existingLocks);
        var resourceKey = BuildResourceKey(resourceType, resourceId);
        return !existingLocks.Any(lockRecord =>
            string.Equals(lockRecord.ResourceKey, resourceKey, StringComparison.Ordinal)
            && lockRecord.IsActive(at));
    }

    public void Renew(
        string ownerTaskNumber,
        int expectedVersion,
        DateTimeOffset renewedAt,
        TimeSpan leaseDuration,
        Guid? lockToken = null)
    {
        EnsureMutationAllowed(ownerTaskNumber, expectedVersion, lockToken);
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), leaseDuration, "Lease duration must be positive.");
        }

        var timestamp = Normalize(renewedAt);
        if (ReleasedAt is not null || ExpiresAt <= timestamp)
        {
            throw new InvalidOperationException("An expired or released resource lock cannot be renewed.");
        }

        ExpiresAt = timestamp.Add(leaseDuration);
        Version = checked(Version + 1);
    }

    public void Release(
        string ownerTaskNumber,
        int expectedVersion,
        DateTimeOffset releasedAt,
        Guid? lockToken = null)
    {
        EnsureMutationAllowed(ownerTaskNumber, expectedVersion, lockToken);
        if (ReleasedAt is not null)
        {
            throw new InvalidOperationException("The resource lock has already been released.");
        }

        ReleasedAt = Normalize(releasedAt);
        Version = checked(Version + 1);
    }

    public static string BuildResourceKey(string resourceType, string resourceId)
        => $"{Require(resourceType, nameof(resourceType))}:{Require(resourceId, nameof(resourceId))}";

    private void EnsureMutationAllowed(string ownerTaskNumber, int expectedVersion, Guid? lockToken)
    {
        if (expectedVersion < 1 || expectedVersion != Version)
        {
            throw new InvalidOperationException(
                $"Resource lock '{ResourceKey}' version conflict; expected {expectedVersion}, actual {Version}.");
        }

        var normalizedOwner = Require(ownerTaskNumber, nameof(ownerTaskNumber));
        if (!string.Equals(OwnerTaskNumber, normalizedOwner, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Only the owning task may mutate a resource lock.");
        }

        if (lockToken.HasValue && lockToken.Value != LockToken)
        {
            throw new InvalidOperationException("The resource lock token does not match.");
        }
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
