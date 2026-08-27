using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Warehouse.Wms.Application.Identity;
using Warehouse.Wms.Infrastructure.Persistence;

namespace Warehouse.Wms.Api.Identity;

public static class IdentityBootstrapCommand
{
    private const string MarkerName = "first-admin-created";

    public static async Task<int> RunAsync(string[] args)
    {
        if (!TryParse(args, out var username, out var warehouse)) return 2;
        if (Console.IsInputRedirected) { Console.Error.WriteLine("Password input must be an interactive console."); return 2; }

        Console.Write("Password: ");
        var password = ReadPassword();
        Console.WriteLine();
        Console.Write("Confirm password: ");
        var confirmation = ReadPassword();
        Console.WriteLine();
        if (!string.Equals(password, confirmation, StringComparison.Ordinal)) { Console.Error.WriteLine("Passwords do not match."); return 2; }

        var configuration = new ConfigurationBuilder().SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true).AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables().Build();
        var connectionString = configuration.GetConnectionString("WmsDb");
        if (string.IsNullOrWhiteSpace(connectionString)) { Console.Error.WriteLine("ConnectionStrings:WmsDb is required."); return 2; }

        var options = new DbContextOptionsBuilder<WarehouseDbContext>().UseSqlServer(connectionString).Options;
        var result = await InitializeAsync(options, username!, password, warehouse!);
        if (result == 0) Console.WriteLine($"Administrator '{username}' created for warehouse '{warehouse}'.");
        else if (result == 1) Console.Error.WriteLine("An administrator already exists.");
        return result;
    }

    public static async Task<int> InitializeAsync(DbContextOptions<WarehouseDbContext> options, string username, string password, string warehouse)
    {
        await using var db = new WarehouseDbContext(options);
        await MigrateUnderServerLockAsync(db);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        await SqlServerApplicationLock.AcquireAsync(db, "WmsIdentityBootstrap");
        if (await db.IdentityBootstrapMarkers.AnyAsync(x => x.Name == MarkerName) || await db.IdentityUsers.AnyAsync(x => db.IdentityUserRoles.Any(link => link.UserId == x.Id && db.IdentityRoles.Any(role => role.Id == link.RoleId && role.NormalizedName == "ADMIN"))))
        {
            return 1;
        }

        PasswordHashing.ValidatePassword(password);
        var admin = new IdentityRoleEntity { Id = Guid.NewGuid(), Name = "Admin", NormalizedName = "ADMIN" };
        var user = new IdentityUserEntity { Id = Guid.NewGuid(), UserId = username!, NormalizedUserId = Key(username), DisplayName = username!, PasswordHash = PasswordHashing.Hash(password) };
        db.IdentityRoles.Add(admin);
        db.IdentityUsers.Add(user);
        db.IdentityUserRoles.Add(new IdentityUserRoleEntity { UserId = user.Id, RoleId = admin.Id });
        db.IdentityWarehouseScopes.Add(new IdentityWarehouseScopeEntity { Id = Guid.NewGuid(), UserId = user.Id, WarehouseId = warehouse!, NormalizedWarehouseId = Key(warehouse) });
        foreach (var permission in new[]
        {
            "Stocktaking.ApplyAdjustment",
            "Task.Cancel",
            "Exception.ConfirmPhysicalResult",
            "Exception.InventoryCorrection",
            "Exception.RequestStop",
            "Task.ManualPhysicalResultConfirmation"
        })
            db.IdentityRolePermissions.Add(new IdentityRolePermissionEntity { Id = Guid.NewGuid(), RoleId = admin.Id, Permission = permission, NormalizedPermission = Key(permission) });
        db.IdentityAudits.Add(new IdentityAuditEntity { Id = Guid.NewGuid(), Action = IdentityAuditAction.UserCreated, UserId = "bootstrap", Target = username!, Succeeded = true, Reason = "initial administrator created", CorrelationId = Guid.NewGuid().ToString("N"), OccurredAt = DateTimeOffset.UtcNow });
        db.IdentityBootstrapMarkers.Add(new IdentityBootstrapMarkerEntity { Name = MarkerName, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return 0;
    }

    private static async Task MigrateUnderServerLockAsync(WarehouseDbContext target)
    {
        var targetConnection = target.Database.GetConnectionString()
            ?? throw new InvalidOperationException("A SQL Server connection string is required for identity bootstrap.");
        var targetBuilder = new SqlConnectionStringBuilder(targetConnection);
        if (string.IsNullOrWhiteSpace(targetBuilder.InitialCatalog))
            throw new InvalidOperationException("The identity bootstrap connection string must specify a database.");

        var masterBuilder = new SqlConnectionStringBuilder(targetConnection) { InitialCatalog = "master" };
        var masterOptions = new DbContextOptionsBuilder<WarehouseDbContext>().UseSqlServer(masterBuilder.ConnectionString).Options;
        await using var master = new WarehouseDbContext(masterOptions);
        await using var transaction = await master.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        await SqlServerApplicationLock.AcquireAsync(master, $"WmsIdentityBootstrapDatabase:{targetBuilder.InitialCatalog}");
        await target.Database.MigrateAsync();
        await transaction.CommitAsync();
    }

    private static bool TryParse(string[] args, out string? username, out string? warehouse)
    {
        username = null; warehouse = "WH-01";
        if (args.Length is < 4 or > 6 || !string.Equals(args[0], "identity", StringComparison.OrdinalIgnoreCase) || !string.Equals(args[1], "create-admin", StringComparison.OrdinalIgnoreCase)) return false;
        for (var index = 2; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length) return false;
            if (string.Equals(args[index], "--username", StringComparison.OrdinalIgnoreCase)) username = args[index + 1];
            else if (string.Equals(args[index], "--warehouse", StringComparison.OrdinalIgnoreCase)) warehouse = args[index + 1];
            else return false;
        }
        return !string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(warehouse);
    }

    private static string ReadPassword()
    {
        var buffer = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) return buffer.ToString();
            if (key.Key == ConsoleKey.Backspace) { if (buffer.Length > 0) buffer.Length--; continue; }
            if (!char.IsControl(key.KeyChar)) buffer.Append(key.KeyChar);
        }
    }

    private static string Key(string value) => value.Trim().ToUpperInvariant();
}
