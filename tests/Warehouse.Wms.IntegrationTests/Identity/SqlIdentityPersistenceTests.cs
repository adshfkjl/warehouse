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
        await using var factory = new TestDbContextFactory(new DbContextOptionsBuilder<WarehouseDbContext>().UseSqlServer(connection).Options);
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
    public async Task Refresh_token_replay_revokes_its_entire_family()
    {
        var connection = NewConnectionString();
        await using var factory = new TestDbContextFactory(new DbContextOptionsBuilder<WarehouseDbContext>().UseSqlServer(connection).Options);
        await using (var db = await factory.CreateDbContextAsync()) await db.Database.MigrateAsync();

        var service = new SqlServerIdentityService(factory, Options());
        service.CreateRole("Operator");
        service.CreateUser(new CreateUserRequest("operator", "Operator", "P@ssw0rd!", ["Operator"], ["WH-01"]));
        var original = await service.LoginAsync(new LoginRequest("operator", "P@ssw0rd!"));
        var successor = await service.RefreshAsync(original.RefreshToken);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RefreshAsync(original.RefreshToken));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RefreshAsync(successor.RefreshToken));
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
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RefreshAsync(successor.RefreshToken));
            await using var db = await factory.CreateDbContextAsync();
            Assert.Equal(2, await db.IdentityRefreshTokens.CountAsync());
            Assert.All(await db.IdentityRefreshTokens.ToListAsync(), token => Assert.NotNull(token.RevokedAt));
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
        await using var factory = new TestDbContextFactory(new DbContextOptionsBuilder<WarehouseDbContext>().UseSqlServer(connection).Options);
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
            var first = await service.GetAuditPageAsync(0, 2);
            var second = await service.GetAuditPageAsync(0, 2);
            Assert.Equal(first, second);
        }
    }

    [SqlServerFact]
    public async Task Concurrent_bootstrap_creates_one_admin_marker_and_explicit_permissions()
    {
        var options = new DbContextOptionsBuilder<WarehouseDbContext>().UseSqlServer(NewConnectionString()).Options;
        await using (var setup = new WarehouseDbContext(options)) await setup.Database.MigrateAsync();
        var results = await Task.WhenAll(
            IdentityBootstrapCommand.InitializeAsync(options, "admin-one", "P@ssw0rd!", "WH-01"),
            IdentityBootstrapCommand.InitializeAsync(options, "admin-two", "P@ssw0rd!", "WH-01"));
        Assert.Equal(1, results.Count(result => result == 0));
        Assert.Equal(1, results.Count(result => result == 1));
        await using var db = new WarehouseDbContext(options);
        Assert.Single(await db.IdentityBootstrapMarkers.ToListAsync());
        Assert.Single(await db.IdentityUsers.ToListAsync());
        Assert.Equal(3, await db.IdentityRolePermissions.CountAsync());
        Assert.Single(await db.IdentityWarehouseScopes.ToListAsync());
        var admin = await db.IdentityUsers.SingleAsync();
        admin.Disabled = true;
        await db.SaveChangesAsync();
        Assert.Equal(1, await IdentityBootstrapCommand.InitializeAsync(options, "replacement", "P@ssw0rd!", "WH-01"));
    }

    private static async Task<(TestDbContextFactory Factory, SqlServerIdentityService Service)> CreateServiceAsync()
    {
        var factory = new TestDbContextFactory(new DbContextOptionsBuilder<WarehouseDbContext>().UseSqlServer(NewConnectionString()).Options);
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

    private static JwtOptions Options() => new("warehouse-tests", "warehouse-tests", "development-only-signing-key-at-least-32-characters-long", TimeSpan.FromMinutes(15), TimeSpan.FromDays(1));

    private sealed class SqlServerFactAttribute : FactAttribute
    {
        public SqlServerFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WMS_SQLSERVER_TEST_CONNECTION")))
                Skip = "Set WMS_SQLSERVER_TEST_CONNECTION to run SQL identity tests.";
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<WarehouseDbContext> options) : IDbContextFactory<WarehouseDbContext>, IAsyncDisposable
    {
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
