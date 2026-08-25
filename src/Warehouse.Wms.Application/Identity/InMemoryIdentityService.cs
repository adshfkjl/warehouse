using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Warehouse.Wms.Application.Authorization;

namespace Warehouse.Wms.Application.Identity;

public sealed class InMemoryIdentityService : IIdentityService
{
    private readonly JwtOptions _options;
    private readonly IAuditLog _audit;
    private readonly Dictionary<string, UserAccount> _users = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RoleDefinitionMutable> _roles = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public InMemoryIdentityService(JwtOptions options, IAuditLog audit)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _roles["Operator"] = new RoleDefinitionMutable("Operator");
        _roles["Supervisor"] = new RoleDefinitionMutable("Supervisor");
        _roles["Admin"] = new RoleDefinitionMutable("Admin");
    }

    public IReadOnlyCollection<RoleDefinition> Roles
    {
        get
        {
            lock (_gate)
            {
                return _roles.Values
                    .Select(role => new RoleDefinition(role.Name, role.Permissions.ToHashSet(StringComparer.OrdinalIgnoreCase)))
                    .ToArray();
            }
        }
    }

    public void CreateUser(CreateUserRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var userId = Require(request.UserId, nameof(request.UserId));
        var displayName = Require(request.DisplayName, nameof(request.DisplayName));
        ValidatePassword(request.Password, nameof(request.Password));
        var roles = NormalizeSet(request.Roles, nameof(request.Roles));
        var warehouses = NormalizeSet(request.WarehouseIds, nameof(request.WarehouseIds));

        lock (_gate)
        {
            if (_users.ContainsKey(userId)) throw new InvalidOperationException($"User '{userId}' already exists.");
            foreach (var role in roles)
            {
                if (!_roles.ContainsKey(role)) throw new KeyNotFoundException($"Role '{role}' was not found.");
            }

            _users.Add(userId, new UserAccount(userId, displayName, HashPassword(request.Password), roles, warehouses));
        }
    }

    public void CreateRole(string roleName)
    {
        var normalized = Require(roleName, nameof(roleName));
        lock (_gate)
        {
            if (_roles.ContainsKey(normalized)) throw new InvalidOperationException($"Role '{normalized}' already exists.");
            _roles.Add(normalized, new RoleDefinitionMutable(normalized));
        }

        _audit.Record(IdentityAuditAction.RoleCreated, "system", normalized, true, "role created");
    }

    public void AssignRole(string userId, string roleName)
    {
        var normalizedUser = Require(userId, nameof(userId));
        var normalizedRole = Require(roleName, nameof(roleName));
        lock (_gate)
        {
            if (!_users.TryGetValue(normalizedUser, out var user)) throw new KeyNotFoundException($"User '{normalizedUser}' was not found.");
            if (!_roles.ContainsKey(normalizedRole)) throw new KeyNotFoundException($"Role '{normalizedRole}' was not found.");
            user.Roles.Add(normalizedRole);
            user.RevokeAllRefreshTokens();
        }

        _audit.Record(IdentityAuditAction.RoleAssigned, normalizedUser, normalizedRole, true, "role assigned");
    }

    public void GrantPermission(string roleName, string permission)
    {
        var normalizedRole = Require(roleName, nameof(roleName));
        var normalizedPermission = Require(permission, nameof(permission));
        lock (_gate)
        {
            if (!_roles.TryGetValue(normalizedRole, out var role)) throw new KeyNotFoundException($"Role '{normalizedRole}' was not found.");
            role.Permissions.Add(normalizedPermission);
        }

        _audit.Record(IdentityAuditAction.PermissionGranted, "system", normalizedRole, true, normalizedPermission);
    }

    public Task<TokenPair> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var userId = Require(request.UserId, nameof(request.UserId));
        UserAccount user;
        lock (_gate)
        {
            if (!_users.TryGetValue(userId, out user!) || user.Disabled || !VerifyPassword(request.Password, user.PasswordHash))
            {
                _audit.Record(IdentityAuditAction.Login, userId, userId, false, "invalid credentials or disabled account");
                throw new UnauthorizedAccessException("Invalid credentials.");
            }

            var pair = IssuePair(user);
            _audit.Record(IdentityAuditAction.Login, userId, userId, true, "login succeeded");
            return Task.FromResult(pair);
        }
    }

    public Task<TokenPair> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = Require(refreshToken, nameof(refreshToken));
        var tokenHash = HashToken(normalized);
        lock (_gate)
        {
            var user = _users.Values.FirstOrDefault(candidate => candidate.RefreshTokens.TryGetValue(tokenHash, out _));
            if (user is null || user.Disabled || !user.RefreshTokens.TryGetValue(tokenHash, out var stored)
                || stored.Revoked || stored.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                _audit.Record(IdentityAuditAction.Refresh, "unknown", "refresh", false, "invalid refresh token");
                throw new UnauthorizedAccessException("Invalid refresh token.");
            }

            stored.Revoked = true;
            var pair = IssuePair(user);
            _audit.Record(IdentityAuditAction.Refresh, user.UserId, user.UserId, true, "refresh token rotated");
            return Task.FromResult(pair);
        }
    }

    public Task LogoutAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = Require(refreshToken, nameof(refreshToken));
        var tokenHash = HashToken(normalized);
        lock (_gate)
        {
            var user = _users.Values.FirstOrDefault(candidate => candidate.RefreshTokens.ContainsKey(tokenHash));
            if (user is null) throw new UnauthorizedAccessException("Invalid refresh token.");
            user.RefreshTokens[tokenHash].Revoked = true;
            _audit.Record(IdentityAuditAction.Logout, user.UserId, user.UserId, true, "logout succeeded");
            return Task.CompletedTask;
        }
    }

    public void ChangePassword(string userId, string currentPassword, string newPassword)
    {
        var normalized = Require(userId, nameof(userId));
        ValidatePassword(newPassword, nameof(newPassword));
        lock (_gate)
        {
            if (!_users.TryGetValue(normalized, out var user) || user.Disabled || !VerifyPassword(currentPassword, user.PasswordHash))
            {
                _audit.Record(IdentityAuditAction.PasswordChanged, normalized, normalized, false, "current password rejected");
                throw new UnauthorizedAccessException("Current password is invalid.");
            }

            user.PasswordHash = HashPassword(newPassword);
            user.RevokeAllRefreshTokens();
        }

        _audit.Record(IdentityAuditAction.PasswordChanged, normalized, normalized, true, "password changed");
    }

    public void DisableUser(string userId, string reason)
    {
        var normalized = Require(userId, nameof(userId));
        var normalizedReason = Require(reason, nameof(reason));
        lock (_gate)
        {
            if (!_users.TryGetValue(normalized, out var user)) throw new KeyNotFoundException($"User '{normalized}' was not found.");
            user.Disabled = true;
            user.RevokeAllRefreshTokens();
        }

        _audit.Record(IdentityAuditAction.UserDisabled, normalized, normalized, true, normalizedReason);
    }

    public AuthenticatedCurrentUser GetCurrentUser(string userId)
    {
        var normalized = Require(userId, nameof(userId));
        lock (_gate)
        {
            if (!_users.TryGetValue(normalized, out var user) || user.Disabled)
                throw new UnauthorizedAccessException("The user account is unavailable.");
            return new AuthenticatedCurrentUser(user.UserId, user.WarehouseIds.ToHashSet(StringComparer.OrdinalIgnoreCase));
        }
    }

    public Task<bool> AuthorizeAsync(
        string operation,
        ICurrentUser user,
        string taskNumber,
        string reason,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(user);
        var normalizedOperation = Require(operation, nameof(operation));
        var normalizedTask = Require(taskNumber, nameof(taskNumber));
        var normalizedReason = Require(reason, nameof(reason));
        var userId = Require(user.UserId, nameof(user));
        bool allowed;
        lock (_gate)
        {
            allowed = _users.TryGetValue(userId, out var account)
                && !account.Disabled
                && HasPermission(account, normalizedOperation)
                && IsWithinWarehouseScope(account, user);
        }

        _audit.Record(IdentityAuditAction.HighRiskAuthorization, userId, normalizedTask, allowed, normalizedReason);
        return Task.FromResult(allowed);
    }

    public void RecordDeviceTaskAudit(string userId, string taskNumber, string reason, bool succeeded = true)
        => _audit.Record(IdentityAuditAction.DeviceTask, Require(userId, nameof(userId)), Require(taskNumber, nameof(taskNumber)), succeeded, Require(reason, nameof(reason)));

    private TokenPair IssuePair(UserAccount user)
    {
        var now = DateTimeOffset.UtcNow;
        var accessExpires = now.Add(_options.AccessTokenLifetime);
        var refreshExpires = now.Add(_options.RefreshTokenLifetime);
        var refreshToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        user.RefreshTokens[HashToken(refreshToken)] = new RefreshTokenRecord(refreshExpires);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.UserId),
            new(ClaimTypes.NameIdentifier, user.UserId),
            new(ClaimTypes.Name, user.UserId),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
        };
        claims.AddRange(user.Roles.Select(role => new Claim(ClaimTypes.Role, role)));
        claims.AddRange(user.WarehouseIds.Select(warehouse => new Claim("warehouse", warehouse)));
        var credentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(_options.Issuer, _options.Audience, claims, now.UtcDateTime, accessExpires.UtcDateTime, credentials);
        return new TokenPair(user.UserId, new JwtSecurityTokenHandler().WriteToken(token), refreshToken, accessExpires, refreshExpires);
    }

    private bool HasPermission(UserAccount user, string operation)
        => user.Roles.Any(role => _roles.TryGetValue(role, out var definition)
            && definition.Permissions.Contains(operation, StringComparer.OrdinalIgnoreCase));

    private static bool IsWithinWarehouseScope(UserAccount account, ICurrentUser user)
        => user is IWarehouseScopedCurrentUser scoped
            && scoped.WarehouseIds.Count > 0
            && scoped.WarehouseIds.Overlaps(account.WarehouseIds);

    private static string HashPassword(string password)
    {
        ValidatePassword(password, nameof(password));
        const int iterations = 120_000;
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
        return $"v1${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    private static bool VerifyPassword(string password, string encoded)
    {
        try
        {
            var parts = encoded.Split('$');
            if (parts.Length != 4 || parts[0] != "v1") return false;
            var iterations = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string HashToken(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static HashSet<string> NormalizeSet(IEnumerable<string>? values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        var result = values.Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (result.Count == 0) throw new ArgumentException("At least one value is required.", parameterName);
        return result;
    }

    private static void ValidatePassword(string? password, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
            throw new ArgumentException("Password must contain at least 8 characters.", parameterName);
    }

    private static string Require(string? value, string parameterName)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty value is required.", parameterName)
            : value.Trim();

    private sealed class RoleDefinitionMutable(string name)
    {
        public string Name { get; } = name;
        public HashSet<string> Permissions { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class UserAccount(
        string userId,
        string displayName,
        string passwordHash,
        IEnumerable<string> roles,
        IEnumerable<string> warehouseIds)
    {
        public string UserId { get; } = userId;
        public string DisplayName { get; } = displayName;
        public string PasswordHash { get; set; } = passwordHash;
        public bool Disabled { get; set; }
        public HashSet<string> Roles { get; } = roles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> WarehouseIds { get; } = warehouseIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, RefreshTokenRecord> RefreshTokens { get; } = new(StringComparer.Ordinal);

        public void RevokeAllRefreshTokens()
        {
            foreach (var token in RefreshTokens.Values) token.Revoked = true;
        }
    }

    private sealed class RefreshTokenRecord(DateTimeOffset expiresAt)
    {
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public bool Revoked { get; set; }
    }
}
