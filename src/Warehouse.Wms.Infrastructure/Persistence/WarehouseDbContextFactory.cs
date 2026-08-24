using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Warehouse.Wms.Infrastructure.Persistence;

public sealed class WarehouseDbContextFactory : IDesignTimeDbContextFactory<WarehouseDbContext>
{
    public WarehouseDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<WarehouseDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=WarehouseWmsDevelopment;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;

        return new WarehouseDbContext(options);
    }
}
