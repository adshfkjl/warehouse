using System.Data;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Warehouse.Wms.Application.Authorization;
using Warehouse.Wms.Application.Identity;

namespace Warehouse.Wms.Infrastructure.Persistence;

public sealed class IdentityUserEntity
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = null!;
    public string NormalizedUserId { get; set; } = null!;
    public string DisplayName { get; set; } = null!;
    public string PasswordHash { get; set; } = null!;
    public bool Disabled { get; set; }
    public long SecurityVersion { get; set; } = 1;
    public int FailedLoginCount { get; set; }
    public DateTimeOffset? FirstFailedLoginAt { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public byte[] Version { get; set; } = null!;
    public List<IdentityUserRoleEntity> Roles { get; } = [];
    public List<IdentityWarehouseScopeEntity> WarehouseScopes { get; } = [];
}

public sealed class IdentityRoleEntity { public Guid Id { get; set; } public string Name { get; set; } = null!; public string NormalizedName { get; set; } = null!; public List<IdentityRolePermissionEntity> Permissions { get; } = []; }
public sealed class IdentityUserRoleEntity { public Guid UserId { get; set; } public Guid RoleId { get; set; } }
public sealed class IdentityRolePermissionEntity { public Guid Id { get; set; } public Guid RoleId { get; set; } public string Permission { get; set; } = null!; public string NormalizedPermission { get; set; } = null!; }
public sealed class IdentityWarehouseScopeEntity { public Guid Id { get; set; } public Guid UserId { get; set; } public string WarehouseId { get; set; } = null!; public string NormalizedWarehouseId { get; set; } = null!; }
public sealed class IdentityRefreshTokenEntity
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = null!;
    public Guid FamilyId { get; set; }
    public Guid? ParentTokenId { get; set; }
    public Guid? ReplacedByTokenId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset? ReuseDetectedAt { get; set; }
    public byte[] Version { get; set; } = null!;
}
public sealed class IdentityAuditEntity
{
    public Guid Id { get; set; }
    public IdentityAuditAction Action { get; set; }
    public string UserId { get; set; } = null!;
    public string Target { get; set; } = null!;
    public bool Succeeded { get; set; }
    public string Reason { get; set; } = null!;
    public string CorrelationId { get; set; } = null!;
    public DateTimeOffset OccurredAt { get; set; }
}
public sealed class IdentityBootstrapMarkerEntity { public string Name { get; set; } = null!; public DateTimeOffset CreatedAt { get; set; } }

public sealed class SqlServerIdentityService : IIdentityService, IAuditLog, IIdentitySecurityValidator
{
    private const string AnonymousActor = "anonymous";
    private readonly IDbContextFactory<WarehouseDbContext> _contexts;
    private readonly JwtOptions _options;
    private readonly IdentitySecurityOptions _security;
    private readonly TimeProvider _timeProvider;

    public SqlServerIdentityService(IDbContextFactory<WarehouseDbContext> contexts, JwtOptions options, IdentitySecurityOptions? security = null, TimeProvider? timeProvider = null)
    {
        _contexts = contexts ?? throw new ArgumentNullException(nameof(contexts));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _security = security ?? IdentitySecurityOptions.Default;
        _security.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public IReadOnlyList<IdentityAuditEntry> Entries
    {
        get
        {
            using var db = _contexts.CreateDbContext();
            return db.IdentityAudits.AsNoTracking().OrderBy(x => x.OccurredAt).ThenBy(x => x.Id)
                .Select(x => new IdentityAuditEntry(x.Action, x.UserId, x.Target, x.Succeeded, x.Reason, x.OccurredAt)).ToArray();
        }
    }

    public IReadOnlyCollection<RoleDefinition> Roles
    {
        get
        {
            using var db = _contexts.CreateDbContext();
            return db.IdentityRoles.AsNoTracking().Include(x => x.Permissions)
                .Select(x => new RoleDefinition(x.Name, x.Permissions.Select(p => p.Permission).ToHashSet(StringComparer.OrdinalIgnoreCase))).ToArray();
        }
    }

    public async Task<IReadOnlyList<IdentityAuditEntry>> GetAuditPageAsync(int skip, int take, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        if (take is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(take));
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);
        return await db.IdentityAudits.AsNoTracking().OrderBy(x => x.OccurredAt).ThenBy(x => x.Id).Skip(skip).Take(take)
            .Select(x => new IdentityAuditEntry(x.Action, x.UserId, x.Target, x.Succeeded, x.Reason, x.OccurredAt)).ToArrayAsync(cancellationToken);
    }

    public void CreateRole(string roleName) => CreateRoleAsync(roleName).GetAwaiter().GetResult();
    public async Task CreateRoleAsync(string roleName, CancellationToken cancellationToken = default)
    {
        var name = Require(roleName, nameof(roleName));
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        if (await db.IdentityRoles.AnyAsync(x => x.NormalizedName == Key(name), cancellationToken)) throw new InvalidOperationException($"Role '{name}' already exists.");
        db.IdentityRoles.Add(new IdentityRoleEntity { Id = Guid.NewGuid(), Name = name, NormalizedName = Key(name) });
        Audit(db, IdentityAuditAction.RoleCreated, AnonymousActor, name, true, "role created");
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public void CreateUser(CreateUserRequest request) => CreateUserAsync(request).GetAwaiter().GetResult();
    public async Task CreateUserAsync(CreateUserRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var userId = Require(request.UserId, nameof(request.UserId));
        var displayName = Require(request.DisplayName, nameof(request.DisplayName));
        PasswordHashing.ValidatePassword(request.Password);
        var roles = Normalize(request.Roles, nameof(request.Roles));
        var scopes = Normalize(request.WarehouseIds, nameof(request.WarehouseIds));
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        if (await db.IdentityUsers.AnyAsync(x => x.NormalizedUserId == Key(userId), cancellationToken)) throw new InvalidOperationException($"User '{userId}' already exists.");
        var roleEntities = await db.IdentityRoles.Where(x => roles.Select(Key).Contains(x.NormalizedName)).ToArrayAsync(cancellationToken);
        if (roleEntities.Length != roles.Length) throw new KeyNotFoundException("A requested role was not found.");
        var account = new IdentityUserEntity { Id = Guid.NewGuid(), UserId = userId, NormalizedUserId = Key(userId), DisplayName = displayName, PasswordHash = PasswordHashing.Hash(request.Password) };
        db.IdentityUsers.Add(account);
        foreach (var role in roleEntities) db.IdentityUserRoles.Add(new IdentityUserRoleEntity { UserId = account.Id, RoleId = role.Id });
        foreach (var scope in scopes) db.IdentityWarehouseScopes.Add(new IdentityWarehouseScopeEntity { Id = Guid.NewGuid(), UserId = account.Id, WarehouseId = scope, NormalizedWarehouseId = Key(scope) });
        Audit(db, IdentityAuditAction.UserCreated, AnonymousActor, userId, true, "user created");
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public void AssignRole(string userId, string roleName) => AssignRoleAsync(userId, roleName).GetAwaiter().GetResult();
    public async Task AssignRoleAsync(string userId, string roleName, CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await AcquireApplicationLockAsync(db, $"WmsIdentityUser:{Key(userId)}", cancellationToken);
        var user = await UserAsync(db, userId, cancellationToken);
        var role = await db.IdentityRoles.SingleOrDefaultAsync(x => x.NormalizedName == Key(roleName), cancellationToken) ?? throw new KeyNotFoundException();
        if (!await db.IdentityUserRoles.AnyAsync(x => x.UserId == user.Id && x.RoleId == role.Id, cancellationToken)) db.IdentityUserRoles.Add(new IdentityUserRoleEntity { UserId = user.Id, RoleId = role.Id });
        Invalidate(user); RevokeAll(db, user.Id, Now());
        Audit(db, IdentityAuditAction.RoleAssigned, user.UserId, role.Name, true, "role assigned");
        await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
    }

    public void GrantPermission(string roleName, string permission) => GrantPermissionAsync(roleName, permission).GetAwaiter().GetResult();
    public async Task GrantPermissionAsync(string roleName, string permission, CancellationToken cancellationToken = default)
    {
        var value = Require(permission, nameof(permission));
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var role = await db.IdentityRoles.SingleOrDefaultAsync(x => x.NormalizedName == Key(roleName), cancellationToken) ?? throw new KeyNotFoundException();
        if (!await db.IdentityRolePermissions.AnyAsync(x => x.RoleId == role.Id && x.NormalizedPermission == Key(value), cancellationToken)) db.IdentityRolePermissions.Add(new IdentityRolePermissionEntity { Id = Guid.NewGuid(), RoleId = role.Id, Permission = value, NormalizedPermission = Key(value) });
        var users = await db.IdentityUsers.Where(user => db.IdentityUserRoles.Any(link => link.UserId == user.Id && link.RoleId == role.Id)).ToArrayAsync(cancellationToken);
        var now = Now(); foreach (var user in users) { Invalidate(user); RevokeAll(db, user.Id, now); }
        Audit(db, IdentityAuditAction.PermissionGranted, AnonymousActor, role.Name, true, value);
        await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
    }

    public async Task<TokenPair> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var requestedId = Require(request.UserId, nameof(request.UserId));
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await AcquireApplicationLockAsync(db, $"WmsIdentityUser:{Key(requestedId)}", cancellationToken);
        var now = Now(); var user = await db.IdentityUsers.SingleOrDefaultAsync(x => x.NormalizedUserId == Key(requestedId), cancellationToken);
        if (user is null || user.Disabled || IsLocked(user, now) || !PasswordHashing.Verify(request.Password, user.PasswordHash, out var needsRehash))
        {
            if (user is not null) RegisterFailure(user, now, db);
            Audit(db, IdentityAuditAction.Login, user?.UserId ?? AnonymousActor, requestedId, false, "invalid credentials");
            await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
            throw new UnauthorizedAccessException("Invalid credentials.");
        }
        ResetFailures(user, db, now); if (needsRehash) user.PasswordHash = PasswordHashing.Hash(request.Password);
        var pair = await IssueAsync(db, user, null, cancellationToken);
        Audit(db, IdentityAuditAction.Login, user.UserId, user.UserId, true, "login succeeded");
        await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
        return pair;
    }

    public async Task<TokenPair> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        var hash = HashToken(refreshToken);
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var lookup = await db.IdentityRefreshTokens.AsNoTracking().SingleOrDefaultAsync(x => x.TokenHash == hash, cancellationToken);
        if (lookup is null)
        {
            Audit(db, IdentityAuditAction.Refresh, AnonymousActor, "refresh", false, "invalid refresh token");
            await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken); throw new UnauthorizedAccessException("Invalid refresh token.");
        }
        var lookupUser = await db.IdentityUsers.AsNoTracking().SingleAsync(x => x.Id == lookup.UserId, cancellationToken);
        await AcquireApplicationLockAsync(db, $"WmsIdentityUser:{lookupUser.NormalizedUserId}", cancellationToken);
        await AcquireApplicationLockAsync(db, $"WmsIdentityRefresh:{hash}", cancellationToken);
        var now = Now();
        var token = await db.IdentityRefreshTokens.SingleAsync(x => x.TokenHash == hash, cancellationToken);
        var user = await db.IdentityUsers.SingleAsync(x => x.Id == token.UserId, cancellationToken);
        if (token.RevokedAt is not null || token.ExpiresAt <= now || user.Disabled)
        {
            RevokeFamily(db, token.FamilyId, now, token.RevokedAt is not null);
            Audit(db, IdentityAuditAction.Refresh, user.UserId, user.UserId, false, token.RevokedAt is not null ? "refresh token replay detected" : "invalid refresh token");
            await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken); throw new UnauthorizedAccessException("Invalid refresh token.");
        }
        var pair = await IssueAsync(db, user, token, cancellationToken); token.RevokedAt = now;
        Audit(db, IdentityAuditAction.Refresh, user.UserId, user.UserId, true, "refresh token rotated");
        await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
        return pair;
    }

    public async Task LogoutAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        var hash = HashToken(refreshToken);
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var token = await db.IdentityRefreshTokens.SingleOrDefaultAsync(x => x.TokenHash == hash, cancellationToken) ?? throw new UnauthorizedAccessException("Invalid refresh token.");
        var user = await db.IdentityUsers.SingleAsync(x => x.Id == token.UserId, cancellationToken);
        RevokeFamily(db, token.FamilyId, Now(), false); Audit(db, IdentityAuditAction.Logout, user.UserId, user.UserId, true, "logout succeeded");
        await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
    }

    public void ChangePassword(string userId, string currentPassword, string newPassword) => ChangePasswordAsync(userId, currentPassword, newPassword).GetAwaiter().GetResult();
    public async Task ChangePasswordAsync(string userId, string currentPassword, string newPassword, CancellationToken cancellationToken = default)
    {
        PasswordHashing.ValidatePassword(newPassword);
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await AcquireApplicationLockAsync(db, $"WmsIdentityUser:{Key(userId)}", cancellationToken);
        var user = await UserAsync(db, userId, cancellationToken);
        if (user.Disabled || !PasswordHashing.Verify(currentPassword, user.PasswordHash, out _))
        {
            Audit(db, IdentityAuditAction.PasswordChanged, user.UserId, user.UserId, false, "current password rejected");
            await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken); throw new UnauthorizedAccessException("Current password is invalid.");
        }
        user.PasswordHash = PasswordHashing.Hash(newPassword); Invalidate(user); RevokeAll(db, user.Id, Now());
        Audit(db, IdentityAuditAction.PasswordChanged, user.UserId, user.UserId, true, "password changed");
        await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
    }

    public void DisableUser(string userId, string reason) => DisableUserAsync(userId, reason).GetAwaiter().GetResult();
    public async Task DisableUserAsync(string userId, string reason, CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await AcquireApplicationLockAsync(db, $"WmsIdentityUser:{Key(userId)}", cancellationToken);
        var user = await UserAsync(db, userId, cancellationToken);
        user.Disabled = true; Invalidate(user); RevokeAll(db, user.Id, Now());
        Audit(db, IdentityAuditAction.UserDisabled, user.UserId, user.UserId, true, Require(reason, nameof(reason)));
        await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
    }

    public AuthenticatedCurrentUser GetCurrentUser(string userId)
    {
        using var db = _contexts.CreateDbContext();
        var user = db.IdentityUsers.SingleOrDefault(x => x.NormalizedUserId == Key(userId)) ?? throw new KeyNotFoundException();
        if (user.Disabled) throw new UnauthorizedAccessException();
        return new AuthenticatedCurrentUser(user.UserId, db.IdentityWarehouseScopes.Where(x => x.UserId == user.Id).Select(x => x.WarehouseId).ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    public async Task<bool> IsAccessTokenCurrentAsync(string userId, long securityVersion, CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);
        return await db.IdentityUsers.AsNoTracking().AnyAsync(x => x.NormalizedUserId == Key(userId) && !x.Disabled && x.SecurityVersion == securityVersion, cancellationToken);
    }

    public async Task<bool> AuthorizeAsync(string operation, ICurrentUser user, string taskNumber, string reason, CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var account = await db.IdentityUsers.SingleOrDefaultAsync(x => x.NormalizedUserId == Key(user.UserId), cancellationToken);
        var scopes = user is IWarehouseScopedCurrentUser scoped ? scoped.WarehouseIds.Select(Key).ToHashSet(StringComparer.Ordinal) : [];
        var allowed = account is not null && !account.Disabled && scopes.Count > 0
            && await db.IdentityWarehouseScopes.AnyAsync(x => x.UserId == account.Id && scopes.Contains(x.NormalizedWarehouseId), cancellationToken)
            && await db.IdentityUserRoles.Where(x => x.UserId == account.Id).Join(db.IdentityRolePermissions, x => x.RoleId, x => x.RoleId, (_, p) => p.NormalizedPermission).AnyAsync(x => x == Key(operation), cancellationToken);
        Audit(db, IdentityAuditAction.HighRiskAuthorization, user.UserId, Require(taskNumber, nameof(taskNumber)), allowed, Require(reason, nameof(reason)));
        await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
        return allowed;
    }

    public void RecordDeviceTaskAudit(string userId, string taskNumber, string reason, bool succeeded = true) => Record(IdentityAuditAction.DeviceTask, userId, taskNumber, succeeded, reason);
    public void Record(IdentityAuditAction action, string userId, string target, bool succeeded, string reason) => RecordAsync(action, userId, target, succeeded, reason).GetAwaiter().GetResult();
    public async Task RecordAsync(IdentityAuditAction action, string userId, string target, bool succeeded, string reason, CancellationToken cancellationToken = default)
    {
        await using var db = await _contexts.CreateDbContextAsync(cancellationToken); Audit(db, action, userId, target, succeeded, reason); await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<TokenPair> IssueAsync(WarehouseDbContext db, IdentityUserEntity user, IdentityRefreshTokenEntity? parent, CancellationToken cancellationToken)
    {
        var now = Now(); var refresh = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        var next = new IdentityRefreshTokenEntity { Id = Guid.NewGuid(), UserId = user.Id, TokenHash = HashToken(refresh), ExpiresAt = now.Add(_options.RefreshTokenLifetime), FamilyId = parent?.FamilyId ?? Guid.NewGuid(), ParentTokenId = parent?.Id };
        db.IdentityRefreshTokens.Add(next); if (parent is not null) parent.ReplacedByTokenId = next.Id;
        var roles = await db.IdentityUserRoles.Where(x => x.UserId == user.Id).Join(db.IdentityRoles, x => x.RoleId, x => x.Id, (_, role) => role.Name).ToArrayAsync(cancellationToken);
        var scopes = await db.IdentityWarehouseScopes.Where(x => x.UserId == user.Id).Select(x => x.WarehouseId).ToArrayAsync(cancellationToken);
        var accessExpires = now.Add(_options.AccessTokenLifetime);
        var claims = new List<Claim> { new(JwtRegisteredClaimNames.Sub, user.UserId), new(ClaimTypes.NameIdentifier, user.UserId), new("security_version", user.SecurityVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)) };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role))); claims.AddRange(scopes.Select(scope => new Claim("warehouse", scope)));
        var jwt = new JwtSecurityToken(_options.Issuer, _options.Audience, claims, now.UtcDateTime, accessExpires.UtcDateTime, new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey)), SecurityAlgorithms.HmacSha256));
        return new TokenPair(user.UserId, new JwtSecurityTokenHandler().WriteToken(jwt), refresh, accessExpires, next.ExpiresAt);
    }

    private void RegisterFailure(IdentityUserEntity user, DateTimeOffset now, WarehouseDbContext db)
    {
        if (user.FirstFailedLoginAt is null || user.FirstFailedLoginAt.Value.Add(_security.FailedLoginWindow) <= now) { user.FailedLoginCount = 0; user.FirstFailedLoginAt = now; }
        user.FailedLoginCount++;
        if (user.FailedLoginCount >= _security.FailedLoginThreshold && user.LockedUntil is null) { user.LockedUntil = now.Add(_security.LockoutDuration); Audit(db, IdentityAuditAction.AccountLocked, user.UserId, user.UserId, false, "too many invalid credentials"); }
    }

    private void ResetFailures(IdentityUserEntity user, WarehouseDbContext db, DateTimeOffset now)
    {
        if (user.LockedUntil is not null && user.LockedUntil <= now) Audit(db, IdentityAuditAction.AccountUnlocked, user.UserId, user.UserId, true, "lockout expired");
        user.FailedLoginCount = 0; user.FirstFailedLoginAt = null; user.LockedUntil = null;
    }

    private static bool IsLocked(IdentityUserEntity user, DateTimeOffset now) => user.LockedUntil is not null && user.LockedUntil > now;
    private static void Invalidate(IdentityUserEntity user) => user.SecurityVersion++;
    private static void RevokeAll(WarehouseDbContext db, Guid userId, DateTimeOffset now) { foreach (var token in db.IdentityRefreshTokens.Where(x => x.UserId == userId && x.RevokedAt == null)) token.RevokedAt = now; }
    private static void RevokeFamily(WarehouseDbContext db, Guid familyId, DateTimeOffset now, bool replay) { foreach (var token in db.IdentityRefreshTokens.Where(x => x.FamilyId == familyId)) { token.RevokedAt ??= now; if (replay) token.ReuseDetectedAt ??= now; } }
    private static async Task<IdentityUserEntity> UserAsync(WarehouseDbContext db, string userId, CancellationToken cancellationToken) => await db.IdentityUsers.SingleOrDefaultAsync(x => x.NormalizedUserId == Key(userId), cancellationToken) ?? throw new KeyNotFoundException();
    private static Task<int> AcquireApplicationLockAsync(WarehouseDbContext db, string resource, CancellationToken cancellationToken)
        => db.Database.ExecuteSqlRawAsync("EXEC sp_getapplock @Resource = {0}, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 30000", [resource], cancellationToken);
    private void Audit(WarehouseDbContext db, IdentityAuditAction action, string actor, string target, bool succeeded, string reason) => db.IdentityAudits.Add(new IdentityAuditEntity { Id = Guid.NewGuid(), Action = action, UserId = Require(actor, nameof(actor)), Target = Require(target, nameof(target)), Succeeded = succeeded, Reason = Require(reason, nameof(reason)), CorrelationId = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N"), OccurredAt = Now() });
    private DateTimeOffset Now() => _timeProvider.GetUtcNow();
    private static string HashToken(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Require(value, nameof(value)))));
    private static string Key(string value) => Require(value, nameof(value)).ToUpperInvariant();
    private static string[] Normalize(IEnumerable<string>? values, string name) { ArgumentNullException.ThrowIfNull(values, name); var result = values.Select(x => Require(x, name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(); if (result.Length == 0) throw new ArgumentException("At least one value is required.", name); return result; }
    private static string Require(string? value, string parameterName) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A non-empty value is required.", parameterName) : value.Trim();
}
