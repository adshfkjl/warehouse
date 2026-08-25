using Warehouse.Wms.Application.Authorization;
using Warehouse.Wms.Application.Identity;

namespace Warehouse.Wms.IntegrationTests.Identity;

public sealed class AuthorizationTests
{
    [Fact]
    public async Task Login_returns_access_and_refresh_tokens_and_records_audit()
    {
        var audit = new InMemoryAuditLog();
        var service = NewService(audit);
        service.CreateUser(new CreateUserRequest("operator", "Operator", "P@ssw0rd!", ["Operator"], ["WH-01"]));

        var tokens = await service.LoginAsync(new LoginRequest("operator", "P@ssw0rd!"));

        Assert.False(string.IsNullOrWhiteSpace(tokens.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(tokens.RefreshToken));
        Assert.Contains(audit.Entries, entry => entry.Action == IdentityAuditAction.Login && entry.Succeeded);
    }

    [Fact]
    public async Task Refresh_rotates_token_and_old_refresh_token_is_rejected()
    {
        var service = NewService();
        service.CreateUser(new CreateUserRequest("operator", "Operator", "P@ssw0rd!", ["Operator"], ["WH-01"]));
        var first = await service.LoginAsync(new LoginRequest("operator", "P@ssw0rd!"));

        var second = await service.RefreshAsync(first.RefreshToken);

        Assert.NotEqual(first.RefreshToken, second.RefreshToken);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RefreshAsync(first.RefreshToken));
    }

    [Fact]
    public async Task Disabled_user_cannot_login_and_logout_revokes_refresh_token()
    {
        var service = NewService();
        service.CreateUser(new CreateUserRequest("operator", "Operator", "P@ssw0rd!", ["Operator"], ["WH-01"]));
        var tokens = await service.LoginAsync(new LoginRequest("operator", "P@ssw0rd!"));
        await service.LogoutAsync(tokens.RefreshToken);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RefreshAsync(tokens.RefreshToken));
        service.DisableUser("operator", "主管停用");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.LoginAsync(new LoginRequest("operator", "P@ssw0rd!")));
    }

    [Fact]
    public async Task High_risk_authorization_requires_permission_and_records_the_operation()
    {
        var audit = new InMemoryAuditLog();
        var service = NewService(audit);
        service.CreateUser(new CreateUserRequest("operator", "Operator", "P@ssw0rd!", ["Operator"], ["WH-01"]));
        var tokens = await service.LoginAsync(new LoginRequest("operator", "P@ssw0rd!"));
        var user = new CurrentUser("operator", new HashSet<string>(["WH-01"], StringComparer.OrdinalIgnoreCase));

        var denied = await service.AuthorizeAsync("Stocktaking.ApplyAdjustment", user, "ST-001", "盘点调整");
        service.GrantPermission("Operator", "Stocktaking.ApplyAdjustment");
        var allowed = await service.AuthorizeAsync("Stocktaking.ApplyAdjustment", user, "ST-001", "盘点调整");

        Assert.False(denied);
        Assert.True(allowed);
        Assert.Contains(audit.Entries, entry => entry.Action == IdentityAuditAction.HighRiskAuthorization && !entry.Succeeded);
        Assert.Contains(audit.Entries, entry => entry.Action == IdentityAuditAction.HighRiskAuthorization && entry.Succeeded);
        Assert.NotEmpty(tokens.AccessToken);
    }

    [Fact]
    public async Task Warehouse_scope_is_required_for_authorized_operation()
    {
        var service = NewService();
        service.CreateUser(new CreateUserRequest("operator", "Operator", "P@ssw0rd!", ["Operator"], ["WH-01"]));
        service.GrantPermission("Operator", "Inventory.Adjust");

        Assert.False(await service.AuthorizeAsync("Inventory.Adjust", new CurrentUser("operator", new HashSet<string>(["WH-02"], StringComparer.OrdinalIgnoreCase)), "TASK-01", "调整"));
        Assert.True(await service.AuthorizeAsync("Inventory.Adjust", new CurrentUser("operator", new HashSet<string>(["WH-01"], StringComparer.OrdinalIgnoreCase)), "TASK-01", "调整"));
    }

    [Fact]
    public async Task Password_change_invalidates_existing_refresh_tokens_and_is_audited()
    {
        var audit = new InMemoryAuditLog();
        var service = NewService(audit);
        service.CreateUser(new CreateUserRequest("operator", "Operator", "P@ssw0rd!", ["Operator"], ["WH-01"]));
        var tokens = await service.LoginAsync(new LoginRequest("operator", "P@ssw0rd!"));

        service.ChangePassword("operator", "P@ssw0rd!", "N3wP@ssw0rd!");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RefreshAsync(tokens.RefreshToken));
        var newLogin = await service.LoginAsync(new LoginRequest("operator", "N3wP@ssw0rd!"));
        Assert.NotEmpty(newLogin.AccessToken);
        Assert.Contains(audit.Entries, entry => entry.Action == IdentityAuditAction.PasswordChanged && entry.Succeeded);
    }

    private static InMemoryIdentityService NewService(InMemoryAuditLog? audit = null)
        => new(new JwtOptions(
            "warehouse-tests",
            "warehouse-tests",
            "development-only-signing-key-at-least-32-characters-long",
            TimeSpan.FromMinutes(15),
            TimeSpan.FromDays(1)), audit ?? new InMemoryAuditLog());

    private sealed record CurrentUser(string UserId, IReadOnlySet<string> WarehouseIds) : IWarehouseScopedCurrentUser;
}
