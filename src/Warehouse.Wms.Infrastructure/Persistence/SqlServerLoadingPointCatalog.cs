using Microsoft.EntityFrameworkCore;
using Warehouse.Wms.Application.Outbound;

namespace Warehouse.Wms.Infrastructure.Persistence;

public sealed class SqlServerLoadingPointCatalog(IDbContextFactory<WarehouseDbContext> dbContextFactory) : ILoadingPointCatalog
{
    public async Task<IReadOnlyList<OutboundLoadingPoint>> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.LoadingPoints.AsNoTracking()
            .OrderBy(point => point.Code)
            .Select(point => new OutboundLoadingPoint(point, false, false, point.IsDisabled, point.IsLocked))
            .ToListAsync(cancellationToken);
    }
}
