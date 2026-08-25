using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Warehouse.Wms.Infrastructure.Persistence;

public sealed class WarehouseDbContextFactory : IDesignTimeDbContextFactory<WarehouseDbContext>
{
    public WarehouseDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__WmsDb")
            ?? "Server=(localdb)\\MSSQLLocalDB;Database=WarehouseWmsDevelopment;Trusted_Connection=True;TrustServerCertificate=True";
        var options = new DbContextOptionsBuilder<WarehouseDbContext>()
            .UseSqlServer(connectionString)
            .Options;

        return new WarehouseDbContext(options);
    }
}
