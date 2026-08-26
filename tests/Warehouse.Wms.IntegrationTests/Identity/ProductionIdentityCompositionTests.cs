using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Warehouse.Wms.Application.Identity;
using Warehouse.Wms.Infrastructure.Persistence;

namespace Warehouse.Wms.IntegrationTests.Identity;

public sealed class ProductionIdentityCompositionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1234567890123456789012345678901")]
    [InlineData("development-only-signing-key-at-least-32-characters-long")]
    public void Production_rejects_missing_blank_short_and_exact_development_jwt_keys(string? key)
    {
        using var factory = CreateFactory(key, null);
        Assert.ThrowsAny<Exception>(() => _ = factory.Services);
    }

    [SqlServerFact]
    public async Task Production_uses_sql_identity_for_a_strong_nondefault_key()
    {
        var connection = NewConnectionString();
        var options = new DbContextOptionsBuilder<WarehouseDbContext>().UseSqlServer(connection).Options;
        await using (var db = new WarehouseDbContext(options)) await db.Database.MigrateAsync();
        using var factory = CreateFactory("development terminology is allowed in a strong unique production signing key", connection);
        using var scope = factory.Services.CreateScope();
        Assert.IsType<SqlServerIdentityService>(scope.ServiceProvider.GetRequiredService<IIdentityService>());
        Assert.IsType<SqlServerIdentityService>(scope.ServiceProvider.GetRequiredService<IAuditLog>());
        Assert.IsType<SqlServerIdentityService>(scope.ServiceProvider.GetRequiredService<IIdentitySecurityValidator>());
    }

    private static WebApplicationFactory<Program> CreateFactory(string? signingKey, string? connection)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Wms:PersistenceMode", "SqlServer");
            builder.UseSetting("ConnectionStrings:WmsDb", connection ?? "Server=127.0.0.1,14333;User Id=sa;Password=WmsDevOnly!123;TrustServerCertificate=True;Initial Catalog=master");
            builder.UseSetting("Jwt:Issuer", "warehouse-tests");
            builder.UseSetting("Jwt:Audience", "warehouse-tests");
            if (signingKey is not null) builder.UseSetting("Jwt:SigningKey", signingKey);
            builder.UseSetting("Jwt:AccessTokenLifetime", "00:15:00");
            builder.UseSetting("Jwt:RefreshTokenLifetime", "1.00:00:00");
        });

    private static string NewConnectionString()
    {
        var configured = Environment.GetEnvironmentVariable("WMS_SQLSERVER_TEST_CONNECTION")!;
        return new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(configured) { InitialCatalog = $"WmsIdentityProduction_{Guid.NewGuid():N}" }.ConnectionString;
    }

    private sealed class SqlServerFactAttribute : FactAttribute
    {
        public SqlServerFactAttribute() { if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WMS_SQLSERVER_TEST_CONNECTION"))) Skip = "Set WMS_SQLSERVER_TEST_CONNECTION to run SQL identity tests."; }
    }
}
