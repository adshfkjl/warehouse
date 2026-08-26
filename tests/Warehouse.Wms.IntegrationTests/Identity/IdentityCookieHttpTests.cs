using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Warehouse.Wms.Application.Identity;
using Warehouse.Wms.Infrastructure.Persistence;

namespace Warehouse.Wms.IntegrationTests.Identity;

public sealed class IdentityCookieHttpTests
{
    [SqlServerFact]
    public async Task Login_refresh_and_logout_keep_refresh_token_in_a_secure_http_only_cookie()
    {
        var connection = NewConnectionString();
        await MigrateAsync(connection);
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Wms:PersistenceMode", "SqlServer");
            builder.UseSetting("ConnectionStrings:WmsDb", connection);
        });
        await SeedAsync(factory);
        using var client = factory.CreateClient();

        using var login = await client.PostAsJsonAsync("/api/users/login", new LoginRequest("operator", "P@ssw0rd!"));
        var loginBody = await login.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.DoesNotContain("refreshToken", loginBody, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("no-store", login.Headers.CacheControl?.ToString());
        var cookie = Assert.Single(login.Headers.GetValues("Set-Cookie"));
        Assert.Contains("wms_refresh=", cookie);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/api/users", cookie, StringComparison.OrdinalIgnoreCase);

        client.DefaultRequestHeaders.Add("Cookie", cookie.Split(';')[0]);
        client.DefaultRequestHeaders.Add("Origin", "http://localhost");
        using var refresh = await client.PostAsync("/api/users/refresh", null);
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        Assert.Equal("no-store", refresh.Headers.CacheControl?.ToString());
        var rotated = Assert.Single(refresh.Headers.GetValues("Set-Cookie"));
        Assert.NotEqual(cookie.Split(';')[0], rotated.Split(';')[0]);

        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", rotated.Split(';')[0]);
        using var logout = await client.PostAsync("/api/users/logout", null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal("no-store", logout.Headers.CacheControl?.ToString());
        var cleared = Assert.Single(logout.Headers.GetValues("Set-Cookie"));
        Assert.Contains("path=/api/users", cleared, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cleared, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cleared, StringComparison.OrdinalIgnoreCase);
    }

    [SqlServerFact]
    public async Task Refresh_rejects_cross_origin_cookie_request()
    {
        var connection = NewConnectionString();
        await MigrateAsync(connection);
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => { builder.UseEnvironment("Testing"); builder.UseSetting("Wms:PersistenceMode", "SqlServer"); builder.UseSetting("ConnectionStrings:WmsDb", connection); });
        await SeedAsync(factory);
        using var client = factory.CreateClient();
        using var login = await client.PostAsJsonAsync("/api/users/login", new LoginRequest("operator", "P@ssw0rd!"));
        client.DefaultRequestHeaders.Add("Cookie", (await login.Content.ReadAsStringAsync()) is not null ? login.Headers.GetValues("Set-Cookie").Single().Split(';')[0] : "");
        client.DefaultRequestHeaders.Add("Origin", "https://attacker.example");
        using var refresh = await client.PostAsync("/api/users/refresh", null);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    private static async Task SeedAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var identity = scope.ServiceProvider.GetRequiredService<IIdentityService>();
        await identity.CreateRoleAsync("Operator");
        await identity.CreateUserAsync(new CreateUserRequest("operator", "Operator", "P@ssw0rd!", ["Operator"], ["WH-01"]));
    }

    private static async Task MigrateAsync(string connection)
    {
        var options = new DbContextOptionsBuilder<WarehouseDbContext>().UseSqlServer(connection).Options;
        await using var db = new WarehouseDbContext(options);
        await db.Database.MigrateAsync();
    }

    private static string NewConnectionString()
    {
        var configured = Environment.GetEnvironmentVariable("WMS_SQLSERVER_TEST_CONNECTION")!;
        return new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(configured) { InitialCatalog = $"WmsIdentityCookie_{Guid.NewGuid():N}" }.ConnectionString;
    }

    private sealed class SqlServerFactAttribute : FactAttribute
    {
        public SqlServerFactAttribute() { if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WMS_SQLSERVER_TEST_CONNECTION"))) Skip = "Set WMS_SQLSERVER_TEST_CONNECTION to run SQL identity tests."; }
    }
}
