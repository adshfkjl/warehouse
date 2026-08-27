using Microsoft.EntityFrameworkCore;
using Warehouse.Wms.Api.Identity;
using Warehouse.Wms.Application.Identity;
using Warehouse.Wms.Infrastructure.Persistence;

namespace Warehouse.Wms.IntegrationTests.Identity;

public sealed class SqlIdentityPersistenceTests
{
    [SqlServerFact]
    public async Task Sql_identity_restarts_rotates_refresh_tokens_and_preserves_audit()
    {
        var connection = NewConnectionString();
        await using var factory = CreateFactory(connection);
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();

        var first = new SqlServerIdentityService(factory, Options());
        first.CreateRole("Operator");
        first.CreateUser(new CreateUserRequest("operator", "Operator", "P@ssw0rd!", ["Operator"], ["WH-01"]));
        first.GrantPermission("Operator", "Stocktaking.ApplyAdjustment");
        var login = await first.LoginAsync(new LoginRequest("operator", "P@ssw0rd!"));

        var restarted = new SqlServerIdentityService(factory, Options());
        var rotated = await restarted.RefreshAsync(login.RefreshToken);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => restarted.RefreshAsync(login.RefreshToken));
        Assert.NotEqual(login.RefreshToken, rotated.RefreshToken);
        Assert.True(await restarted.AuthorizeAsync("Stocktaking.ApplyAdjustment", new AuthenticatedCurrentUser("operator", new HashSet<string>(["WH-01"])), "ST-001", "adjust"));
        Assert.Contains(restarted.Entries, entry => entry.Action == IdentityAuditAction.Refresh && entry.Succeeded);
    }

    [SqlServerFact]
    public async Task Refresh_token_replay_is_rejected_without_revoking_the_current_successor()
    {
        var connection = NewConnectionString();
        await using var factory = CreateFactory(connection);
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();

        var service = new SqlServerIdentityService(factory, Options());
        service.CreateRole("Operator");
        service.CreateUser(new CreateUserRequest("operator", "Operator", "P@ssw0rd!", ["Operator"], ["WH-01"]));
        var original = await service.LoginAsync(new LoginRequest("operator", "P@ssw0rd!"));
        var successor = await service.RefreshAsync(original.RefreshToken);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RefreshAsync(original.RefreshToken));
        await service.RefreshAsync(successor.RefreshToken);
    }

    [SqlServerFact]
    public async Task Sixteen_concurrent_refreshes_create_exactly_one_successor()
    {
        var (factory, service) = await CreateServiceAsync();
        await using (factory)
        {
            var login = await service.LoginAsync(new LoginRequest("operator", "P@ssw0rd!"));
            var attempts = await Task.WhenAll(Enumerable.Range(0, 16).Select(async _ =>
            {
                try { return await service.RefreshAsync(login.RefreshToken); }
                catch (UnauthorizedAccessException) { return null; }
            }));
            var successor = Assert.Single(attempts.Where(token => token is not null))!;
            await service.RefreshAsync(successor.RefreshToken);
            await using var db = await factory.CreateDbContextAsync();
            Assert.Equal(3, await db.IdentityRefreshTokens.CountAsync());
            Assert.Single(await db.IdentityRefreshTokens.Where(token => token.RevokedAt == null).ToListAsync());
        }
    }

    [SqlServerFact]
    public async Task Disable_or_password_change_invalidates_access_and_refresh_tokens()
    {
        var (factory, service) = await CreateServiceAsync();
        await using (factory)
        {
            var login = await service.LoginAsync(new LoginRequest("operator", "P@ssw0rd!"));
            await service.DisableUserAsync("operator", "test");
            Assert.False(await service.IsAccessTokenCurrentAsync("operator", 1));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RefreshAsync(login.RefreshToken));

            var (secondFactory, secondService) = await CreateServiceAsync();
            await using (secondFactory)
            {
                var secondLogin = await secondService.LoginAsync(new LoginRequest("operator", "P@ssw0rd!"));
                await secondService.ChangePasswordAsync("operator", "P@ssw0rd!", "N3wP@ssw0rd!");
                Assert.False(await secondService.IsAccessTokenCurrentAsync("operator", 1));
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => secondService.RefreshAsync(secondLogin.RefreshToken));
            }
        }
    }

    [SqlServerFact]
    public async Task Concurrent_refresh_and_disable_leaves_no_valid_access_or_refresh_token()
    {
        await AssertSecurityMutationWinsAsync(service => service.DisableUserAsync("operator", "test"));
    }

    [SqlServerFact]
    public async Task Concurrent_refresh_and_password_change_leaves_no_valid_access_or_refresh_token()
    {
        await AssertSecurityMutationWinsAsync(service => service.ChangePasswordAsync("operator", "P@ssw0rd!", "N3wP@ssw0rd!"));
    }

    [SqlServerFact]
    public async Task Concurrent_refresh_and_role_change_leaves_no_valid_access_or_refresh_token()
    {
        await AssertSecurityMutationWinsAsync(service => service.AssignRoleAsync("operator", "Supervisor"));
    }

    [SqlServerFact]
    public async Task Grant_permission_waits_for_each_affected_user_lock()
    {
        var (factory, service) = await CreateServiceAsync();
        await using (factory)
        await using (var connection = new Microsoft.Data.SqlClient.SqlConnection(NewConnectionStringForExistingDatabase(factory)))
        {
            await connection.OpenAsync();
            await using var transaction = (Microsoft.Data.SqlClient.SqlTransaction)await connection.BeginTransactionAsync();
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "DECLARE @result int; EXEC @result = sp_getapplock @Resource = N'WmsIdentityUser:OPERATOR', @LockMode = N'Exclusive', @LockOwner = N'Transaction'; SELECT @result;";
                Assert.True(Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) >= 0);
            }

            var grant = service.GrantPermissionAsync("Operator", "Inventory.Adjust", "admin");
            await Task.Delay(250);
            Assert.False(grant.IsCompleted);
            await transaction.CommitAsync();
            await grant;
        }
    }

    [SqlServerFact]
    public async Task High_risk_authorization_waits_for_the_user_security_lock()
    {
        var (factory, service) = await CreateServiceAsync();
        service.GrantPermission("Operator", "Inventory.Adjust");
        await using (factory)
        await using (var connection = new Microsoft.Data.SqlClient.SqlConnection(NewConnectionStringForExistingDatabase(factory)))
        {
            await connection.OpenAsync();
            await using var transaction = (Microsoft.Data.SqlClient.SqlTransaction)await connection.BeginTransactionAsync();
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "DECLARE @result int; EXEC @result = sp_getapplock @Resource = N'WmsIdentityUser:OPERATOR', @LockMode = N'Exclusive', @LockOwner = N'Transaction'; SELECT @result;";
                Assert.True(Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) >= 0);
            }

            var authorization = service.AuthorizeAsync(
                "Inventory.Adjust",
                new AuthenticatedCurrentUser("operator", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "WH-01" }),
                "TASK-LOCK",
                "test");
            await Task.Delay(250);
            Assert.False(authorization.IsCompleted);
            await transaction.CommitAsync();
            Assert.True(await authorization);
        }
    }

    [Fact]
    public void Negative_application_lock_result_fails_closed()
    {
        Assert.Throws<InvalidOperationException>(() => SqlServerApplicationLock.ThrowIfNotAcquired(-1, "test-lock"));
    }

    [SqlServerFact]
    public async Task Concurrent_login_and_disable_never_leaves_a_usable_credential()
    {
        var (factory, service) = await CreateServiceAsync();
        await using (factory)
        {
            var logins = Enumerable.Range(0, 8).Select(async _ =>
            {
                try { return await service.LoginAsync(new LoginRequest("operator", "P@ssw0rd!")); }
                catch (UnauthorizedAccessException) { return null; }
            });
            var disable = service.DisableUserAsync("operator", "test");
            var tokens = await Task.WhenAll(logins);
            await disable;
            Assert.False(await service.IsAccessTokenCurrentAsync("operator", 1));
            foreach (var token in tokens.Where(token => token is not null))
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RefreshAsync(token!.RefreshToken));
        }
    }

    [SqlServerFact]
    public async Task Login_lockout_persists_and_recovers_using_time_provider()
    {
        var connection = NewConnectionString();
        await using var factory = CreateFactory(connection);
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-27T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var service = new SqlServerIdentityService(factory, Options(), new IdentitySecurityOptions(2, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10)), clock);
        service.CreateRole("Operator");
        service.CreateUser(new CreateUserRequest("operator", "Operator", "P@ssw0rd!", ["Operator"], ["WH-01"]));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.LoginAsync(new LoginRequest("operator", "bad-password")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.LoginAsync(new LoginRequest("operator", "bad-password")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.LoginAsync(new LoginRequest("operator", "P@ssw0rd!")));
        clock.Advance(TimeSpan.FromMinutes(10));
        var restarted = new SqlServerIdentityService(factory, Options(), new IdentitySecurityOptions(2, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10)), clock);
        await restarted.LoginAsync(new LoginRequest("operator", "P@ssw0rd!"));
        Assert.Contains(restarted.Entries, entry => entry.Action == IdentityAuditAction.AccountLocked);
        Assert.Contains(restarted.Entries, entry => entry.Action == IdentityAuditAction.AccountUnlocked);
    }

    [SqlServerFact]
    public async Task Login_lockout_reenters_after_expiry_without_a_successful_login()
    {
        var connection = NewConnectionString();
        await using var factory = CreateFactory(connection);
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-27T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var security = new IdentitySecurityOptions(2, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));
        var service = new SqlServerIdentityService(factory, Options(), security, clock);
        service.CreateRole("Operator");
        service.CreateUser(new CreateUserRequest("operator", "Operator", "P@ssw0rd!", ["Operator"], ["WH-01"]));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.LoginAsync(new LoginRequest("operator", "bad-password")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.LoginAsync(new LoginRequest("operator", "bad-password")));
        clock.Advance(TimeSpan.FromMinutes(10));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.LoginAsync(new LoginRequest("operator", "bad-password")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.LoginAsync(new LoginRequest("operator", "bad-password")));

        await using var verification = await factory.CreateDbContextAsync();
        var user = await verification.IdentityUsers.SingleAsync(user => user.UserId == "operator");
        Assert.True(user.LockedUntil > clock.GetUtcNow());
    }

    [SqlServerFact]
    public async Task Administrative_password_change_audit_tracks_actor_and_target()
    {
        var (factory, service) = await CreateServiceAsync();
        await using (factory)
        {
            await service.ChangePasswordAsync("operator", "P@ssw0rd!", "N3wP@ssw0rd!", "admin");

            var audit = await service.GetAuditPageAsync(0, 100);
            Assert.Contains(audit, entry => entry.Action == IdentityAuditAction.PasswordChanged
                && entry.UserId == "admin"
                && entry.Target == "operator"
                && entry.Succeeded);
        }
    }

    [SqlServerFact]
    public async Task Self_service_password_change_keeps_the_user_as_audit_actor()
    {
        var (factory, service) = await CreateServiceAsync();
        await using (factory)
        {
            await service.ChangePasswordAsync("operator", "P@ssw0rd!", "N3wP@ssw0rd!");

            var audit = await service.GetAuditPageAsync(0, 100);
            Assert.Contains(audit, entry => entry.Action == IdentityAuditAction.PasswordChanged
                && entry.UserId == "operator"
                && entry.Target == "operator"
                && entry.Succeeded);
        }
    }

    [SqlServerFact]
    public async Task Audit_table_rejects_update_and_delete()
    {
        var (factory, service) = await CreateServiceAsync();
        await using (factory)
        {
            service.Record(IdentityAuditAction.DeviceTask, "operator", "T-1", true, "test");
            await using var db = await factory.CreateDbContextAsync();
            var id = await db.IdentityAudits.Select(x => x.Id).FirstAsync();
            await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => db.Database.ExecuteSqlRawAsync("UPDATE dbo.IdentityAudits SET Reason = N'x' WHERE Id = {0}", id));
            await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM dbo.IdentityAudits WHERE Id = {0}", id));
        }
    }

    [SqlServerFact]
    public async Task Audit_pages_have_stable_time_and_id_order()
    {
        var (factory, service) = await CreateServiceAsync();
        await using (factory)
        {
            service.Record(IdentityAuditAction.DeviceTask, "operator", "T-1", true, "one");
            service.Record(IdentityAuditAction.DeviceTask, "operator", "T-2", true, "two");
            var first = await service.GetAuditPageAsync(0, 100);
            var second = await service.GetAuditPageAsync(0, 100);
            Assert.Equal(first, second);
            await using var db = await factory.CreateDbContextAsync();
            var persisted = await db.IdentityAudits.SingleAsync(entry => entry.Target == "T-1");
            Assert.Equal(persisted.CorrelationId, first.Single(entry => entry.Target == "T-1").CorrelationId);
        }
    }

    [SqlServerFact]
    public async Task Concurrent_bootstrap_creates_one_admin_marker_and_explicit_permissions()
    {
        var options = new DbContextOptionsBuilder<WarehouseDbContext>().UseSqlServer(NewConnectionString()).Options;
        var results = await Task.WhenAll(
            IdentityBootstrapCommand.InitializeAsync(options, "admin-one", "P@ssw0rd!", "WH-01"),
            IdentityBootstrapCommand.InitializeAsync(options, "admin-two", "P@ssw0rd!", "WH-01"));
        Assert.Equal(1, results.Count(result => result == 0));
        Assert.Equal(1, results.Count(result => result == 1));
        await using var db = new WarehouseDbContext(options);
        Assert.Single(await db.IdentityBootstrapMarkers.ToListAsync());
        Assert.Single(await db.IdentityUsers.ToListAsync());
        var permissions = await db.IdentityRolePermissions.Select(permission => permission.Permission).ToArrayAsync();
        Assert.Contains("Exception.ConfirmPhysicalResult", permissions);
        Assert.Contains("Exception.InventoryCorrection", permissions);
        Assert.Contains("Exception.RequestStop", permissions);
        Assert.Contains("Task.ManualPhysicalResultConfirmation", permissions);
        Assert.Single(await db.IdentityWarehouseScopes.ToListAsync());
        var admin = await db.IdentityUsers.SingleAsync();
        admin.Disabled = true;
        await db.SaveChangesAsync();
        Assert.Equal(1, await IdentityBootstrapCommand.InitializeAsync(options, "replacement", "P@ssw0rd!", "WH-01"));
    }

    private static async Task<(TestDbContextFactory Factory, SqlServerIdentityService Service)> CreateServiceAsync()
    {
        var factory = CreateFactory(NewConnectionString());
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();
        var service = new SqlServerIdentityService(factory, Options());
        service.CreateRole("Operator");
        service.CreateUser(new CreateUserRequest("operator", "Operator", "P@ssw0rd!", ["Operator"], ["WH-01"]));
        return (factory, service);
    }

    private static async Task AssertSecurityMutationWinsAsync(Func<SqlServerIdentityService, Task> mutation)
    {
        var (factory, service) = await CreateServiceAsync();
        await using (factory)
        {
            service.CreateRole("Supervisor");
            var login = await service.LoginAsync(new LoginRequest("operator", "P@ssw0rd!"));
            var refresh = service.RefreshAsync(login.RefreshToken);
            var change = mutation(service);
            try { await refresh; } catch (UnauthorizedAccessException) { }
            await change;
            Assert.False(await service.IsAccessTokenCurrentAsync("operator", 1));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RefreshAsync(login.RefreshToken));
        }
    }

    private static string NewConnectionString()
    {
        var configured = Environment.GetEnvironmentVariable("WMS_SQLSERVER_TEST_CONNECTION")!;
        return new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(configured) { InitialCatalog = $"WmsIdentity_{Guid.NewGuid():N}" }.ConnectionString;
    }

    private static string NewConnectionStringForExistingDatabase(TestDbContextFactory factory)
        => factory.ConnectionString;

    private static TestDbContextFactory CreateFactory(string connectionString)
        => new(new DbContextOptionsBuilder<WarehouseDbContext>().UseSqlServer(connectionString).Options, connectionString);

    private static JwtOptions Options() => new("warehouse-tests", "warehouse-tests", "development-only-signing-key-at-least-32-characters-long", TimeSpan.FromMinutes(15), TimeSpan.FromDays(1));

    private sealed class SqlServerFactAttribute : FactAttribute
    {
        public SqlServerFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WMS_SQLSERVER_TEST_CONNECTION")))
                Skip = "Set WMS_SQLSERVER_TEST_CONNECTION to run SQL identity tests.";
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<WarehouseDbContext> options, string connectionString) : IDbContextFactory<WarehouseDbContext>, IAsyncDisposable
    {
        public string ConnectionString { get; } = connectionString;
        public WarehouseDbContext CreateDbContext() => new(options);
        public Task<WarehouseDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WarehouseDbContext(options));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }
}
