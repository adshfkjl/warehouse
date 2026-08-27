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

    [Fact]
    public async Task Actor_aware_async_identity_mutations_preserve_the_authenticated_actor()
    {
        var audit = new InMemoryAuditLog();
        IIdentityService service = AsIdentityService(NewService(audit));

        await service.CreateRoleAsync("Reviewer", "admin");
        await service.CreateUserAsync(new CreateUserRequest("target", "Target", "P@ssw0rd!", ["Reviewer"], ["WH-01"]), "admin");
        await service.AssignRoleAsync("target", "Supervisor", "admin");
        await service.GrantPermissionAsync("Reviewer", "Inventory.Adjust", "admin");
        await service.DisableUserAsync("target", "administrative disable", "admin");

        Assert.Contains(audit.Entries, entry => entry.Action == IdentityAuditAction.RoleCreated && entry.UserId == "admin" && entry.Target == "Reviewer");
        Assert.Contains(audit.Entries, entry => entry.Action == IdentityAuditAction.UserCreated && entry.UserId == "admin" && entry.Target == "target");
        Assert.Contains(audit.Entries, entry => entry.Action == IdentityAuditAction.RoleAssigned && entry.UserId == "admin" && entry.Target == "target" && entry.Reason.Contains("Supervisor", StringComparison.Ordinal));
        Assert.Contains(audit.Entries, entry => entry.Action == IdentityAuditAction.PermissionGranted && entry.UserId == "admin" && entry.Target == "Reviewer" && entry.Reason.Contains("Inventory.Adjust", StringComparison.Ordinal));
        Assert.Contains(audit.Entries, entry => entry.Action == IdentityAuditAction.UserDisabled && entry.UserId == "admin" && entry.Target == "target" && entry.Reason == "administrative disable");
    }

    [Fact]
    public async Task Actor_aware_password_changes_attribute_administrator_and_self_service_to_the_actor()
    {
        var audit = new InMemoryAuditLog();
        IIdentityService service = AsIdentityService(NewService(audit));
        service.CreateUser(new CreateUserRequest("admin", "Admin", "P@ssw0rd!", ["Admin"], ["WH-01"]));
        service.CreateUser(new CreateUserRequest("target", "Target", "P@ssw0rd!", ["Operator"], ["WH-01"]));

        await service.ChangePasswordAsync("target", "P@ssw0rd!", "N3wP@ssw0rd!", "admin");
        await service.ChangePasswordAsync("target", "N3wP@ssw0rd!", "S3lfP@ssw0rd!", "target");

        Assert.Contains(audit.Entries, entry => entry.Action == IdentityAuditAction.PasswordChanged && entry.Succeeded && entry.UserId == "admin" && entry.Target == "target");
        Assert.Contains(audit.Entries, entry => entry.Action == IdentityAuditAction.PasswordChanged && entry.Succeeded && entry.UserId == "target" && entry.Target == "target");
    }

    [Fact]
    public async Task Refresh_replay_is_audited_without_revoking_the_successor()
    {
        var audit = new InMemoryAuditLog();
        var service = NewService(audit);
        service.CreateUser(new CreateUserRequest("operator", "Operator", "P@ssw0rd!", ["Operator"], ["WH-01"]));
        var first = await service.LoginAsync(new LoginRequest("operator", "P@ssw0rd!"));
        var successor = await service.RefreshAsync(first.RefreshToken);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RefreshAsync(first.RefreshToken));
        var successorRotation = await service.RefreshAsync(successor.RefreshToken);

        Assert.NotEqual(successor.RefreshToken, successorRotation.RefreshToken);
        Assert.Contains(audit.Entries, entry => entry.Action == IdentityAuditAction.Refresh && !entry.Succeeded && entry.Reason.Contains("replay", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Logout_with_a_rotated_refresh_token_revokes_the_entire_token_family()
    {
        var audit = new InMemoryAuditLog();
        var service = NewService(audit);
        service.CreateUser(new CreateUserRequest("operator", "Operator", "P@ssw0rd!", ["Operator"], ["WH-01"]));
        var first = await service.LoginAsync(new LoginRequest("operator", "P@ssw0rd!"));
        var successor = await service.RefreshAsync(first.RefreshToken);

        await service.LogoutAsync(first.RefreshToken);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RefreshAsync(successor.RefreshToken));
        Assert.Contains(audit.Entries, entry => entry.Action == IdentityAuditAction.Logout && entry.Succeeded && entry.Target == "operator");
    }

    [Fact]
    public async Task Actor_aware_permission_grant_revokes_affected_users_refresh_tokens()
    {
        var audit = new InMemoryAuditLog();
        IIdentityService service = AsIdentityService(NewService(audit));
        await service.CreateUserAsync(new CreateUserRequest("operator", "Operator", "P@ssw0rd!", ["Operator"], ["WH-01"]), "admin");
        var login = await service.LoginAsync(new LoginRequest("operator", "P@ssw0rd!"));

        await service.GrantPermissionAsync("Operator", "Inventory.Adjust", "admin");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RefreshAsync(login.RefreshToken));
        Assert.Contains(audit.Entries, entry => entry.Action == IdentityAuditAction.PermissionGranted && entry.UserId == "admin" && entry.Target == "Operator");
    }

    private static InMemoryIdentityService NewService(InMemoryAuditLog? audit = null)
        => new(new JwtOptions(
            "warehouse-tests",
            "warehouse-tests",
            "development-only-signing-key-at-least-32-characters-long",
            TimeSpan.FromMinutes(15),
            TimeSpan.FromDays(1)), audit ?? new InMemoryAuditLog());

#pragma warning disable CA1859 // The test must dispatch through IIdentityService to cover its default overloads.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static IIdentityService AsIdentityService(InMemoryIdentityService service) => service;
#pragma warning restore CA1859

    private sealed record CurrentUser(string UserId, IReadOnlySet<string> WarehouseIds) : IWarehouseScopedCurrentUser;
}
