using Warehouse.Wms.Application.Authorization;

namespace Warehouse.Wms.Application.Identity;

public sealed record JwtOptions(
    string Issuer,
    string Audience,
    string SigningKey,
    TimeSpan AccessTokenLifetime,
    TimeSpan RefreshTokenLifetime)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Issuer)) throw new ArgumentException("JWT issuer is required.", nameof(Issuer));
        if (string.IsNullOrWhiteSpace(Audience)) throw new ArgumentException("JWT audience is required.", nameof(Audience));
        if (string.IsNullOrWhiteSpace(SigningKey) || SigningKey.Length < 32)
            throw new ArgumentException("JWT signing key must contain at least 32 characters.", nameof(SigningKey));
        if (AccessTokenLifetime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(AccessTokenLifetime));
        if (RefreshTokenLifetime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(RefreshTokenLifetime));
    }
}

public sealed record CreateUserRequest(
    string UserId,
    string DisplayName,
    string Password,
    IReadOnlyCollection<string> Roles,
    IReadOnlyCollection<string> WarehouseIds);

public sealed record LoginRequest(string UserId, string Password);

public sealed record TokenPair(
    string UserId,
    string AccessToken,
    string RefreshToken,
    DateTimeOffset AccessTokenExpiresAt,
    DateTimeOffset RefreshTokenExpiresAt);

public sealed record AccessTokenResponse(
    string UserId,
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt);

public sealed record IdentitySecurityOptions(
    int FailedLoginThreshold,
    TimeSpan FailedLoginWindow,
    TimeSpan LockoutDuration)
{
    public static IdentitySecurityOptions Default { get; } = new(5, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15));

    public void Validate()
    {
        if (FailedLoginThreshold < 1) throw new ArgumentOutOfRangeException(nameof(FailedLoginThreshold));
        if (FailedLoginWindow <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(FailedLoginWindow));
        if (LockoutDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(LockoutDuration));
    }
}

public interface IIdentitySecurityValidator
{
    Task<bool> IsAccessTokenCurrentAsync(string userId, long securityVersion, CancellationToken cancellationToken = default);
}

public sealed record RoleDefinition(string Name, IReadOnlySet<string> Permissions);

public interface IWarehouseScopedCurrentUser : ICurrentUser
{
    IReadOnlySet<string> WarehouseIds { get; }
}

public sealed record AuthenticatedCurrentUser(
    string UserId,
    IReadOnlySet<string> WarehouseIds) : IWarehouseScopedCurrentUser;

public enum IdentityAuditAction
{
    UserCreated,
    Login,
    Refresh,
    Logout,
    PasswordChanged,
    UserDisabled,
    RoleCreated,
    RoleAssigned,
    PermissionGranted,
    HighRiskAuthorization,
    DeviceTask,
    AccountLocked,
    AccountUnlocked
}

public sealed record IdentityAuditEntry(
    IdentityAuditAction Action,
    string UserId,
    string Target,
    bool Succeeded,
    string Reason,
    DateTimeOffset OccurredAt,
    string CorrelationId);

public interface IAuditLog
{
    IReadOnlyList<IdentityAuditEntry> Entries { get; }

    void Record(
        IdentityAuditAction action,
        string userId,
        string target,
        bool succeeded,
        string reason);

    Task RecordAsync(IdentityAuditAction action, string userId, string target, bool succeeded, string reason, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Record(action, userId, target, succeeded, reason);
        return Task.CompletedTask;
    }
}

public sealed class InMemoryAuditLog : IAuditLog
{
    private readonly object _gate = new();
    private readonly List<IdentityAuditEntry> _entries = [];

    public IReadOnlyList<IdentityAuditEntry> Entries
    {
        get { lock (_gate) return _entries.ToArray(); }
    }

    public void Record(IdentityAuditAction action, string userId, string target, bool succeeded, string reason)
    {
        lock (_gate)
        {
            _entries.Add(new IdentityAuditEntry(
                action,
                Require(userId, nameof(userId)),
                Require(target, nameof(target)),
                succeeded,
                Require(reason, nameof(reason)),
                DateTimeOffset.UtcNow,
                Guid.NewGuid().ToString("N")));
        }
    }

    private static string Require(string? value, string parameterName)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty value is required.", parameterName)
            : value.Trim();
}

public interface IIdentityService : IRiskAuthorizationService
{
    IReadOnlyCollection<RoleDefinition> Roles { get; }

    Task<IReadOnlyCollection<RoleDefinition>> GetRolesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Roles);
    }

    void CreateUser(CreateUserRequest request);

    void CreateRole(string roleName);

    void AssignRole(string userId, string roleName);

    void GrantPermission(string roleName, string permission);

    Task<TokenPair> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);

    Task<TokenPair> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default);

    Task LogoutAsync(string refreshToken, CancellationToken cancellationToken = default);

    void ChangePassword(string userId, string currentPassword, string newPassword);

    void DisableUser(string userId, string reason);

    AuthenticatedCurrentUser GetCurrentUser(string userId);

    void RecordDeviceTaskAudit(string userId, string taskNumber, string reason, bool succeeded = true);

    Task CreateUserAsync(CreateUserRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CreateUser(request);
        return Task.CompletedTask;
    }

    Task CreateUserAsync(CreateUserRequest request, string actorUserId, CancellationToken cancellationToken = default)
        => CreateUserAsync(request, cancellationToken);

    Task CreateRoleAsync(string roleName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CreateRole(roleName);
        return Task.CompletedTask;
    }

    Task CreateRoleAsync(string roleName, string actorUserId, CancellationToken cancellationToken = default)
        => CreateRoleAsync(roleName, cancellationToken);

    Task AssignRoleAsync(string userId, string roleName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AssignRole(userId, roleName);
        return Task.CompletedTask;
    }

    Task AssignRoleAsync(string userId, string roleName, string actorUserId, CancellationToken cancellationToken = default)
        => AssignRoleAsync(userId, roleName, cancellationToken);

    Task GrantPermissionAsync(string roleName, string permission, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GrantPermission(roleName, permission);
        return Task.CompletedTask;
    }

    Task GrantPermissionAsync(string roleName, string permission, string actorUserId, CancellationToken cancellationToken = default)
        => GrantPermissionAsync(roleName, permission, cancellationToken);

    Task ChangePasswordAsync(string userId, string currentPassword, string newPassword, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ChangePassword(userId, currentPassword, newPassword);
        return Task.CompletedTask;
    }

    Task ChangePasswordAsync(string userId, string currentPassword, string newPassword, string actorUserId, CancellationToken cancellationToken = default)
        => ChangePasswordAsync(userId, currentPassword, newPassword, cancellationToken);

    Task DisableUserAsync(string userId, string reason, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DisableUser(userId, reason);
        return Task.CompletedTask;
    }

    Task DisableUserAsync(string userId, string reason, string actorUserId, CancellationToken cancellationToken = default)
        => DisableUserAsync(userId, reason, cancellationToken);
}
