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
    Login,
    Refresh,
    Logout,
    PasswordChanged,
    UserDisabled,
    RoleCreated,
    RoleAssigned,
    PermissionGranted,
    HighRiskAuthorization,
    DeviceTask
}

public sealed record IdentityAuditEntry(
    IdentityAuditAction Action,
    string UserId,
    string Target,
    bool Succeeded,
    string Reason,
    DateTimeOffset OccurredAt);

public interface IAuditLog
{
    IReadOnlyList<IdentityAuditEntry> Entries { get; }

    void Record(
        IdentityAuditAction action,
        string userId,
        string target,
        bool succeeded,
        string reason);
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
                DateTimeOffset.UtcNow));
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
}
