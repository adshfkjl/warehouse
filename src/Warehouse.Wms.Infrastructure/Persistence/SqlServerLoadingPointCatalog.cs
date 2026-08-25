using Microsoft.EntityFrameworkCore;
using Warehouse.Wms.Application.Outbound;

namespace Warehouse.Wms.Infrastructure.Persistence;

public sealed class SqlServerLoadingPointCatalog(IDbContextFactory<WarehouseDbContext> dbContextFactory, ILoadingPointRuntimeStatus runtimeStatus) : ILoadingPointCatalog
{
    public async Task<IReadOnlyList<OutboundLoadingPoint>> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var occupied = await db.Pallets.AsNoTracking().Where(p => p.CurrentLoadingPointId != null)
            .Select(p => p.CurrentLoadingPointId!.Value).ToListAsync(cancellationToken);
        var occupiedIds = occupied.ToHashSet();
        var points = await db.LoadingPoints.AsNoTracking()
            .OrderBy(point => point.Code)
            .ToListAsync(cancellationToken);
        return points.Select(point => new OutboundLoadingPoint(
            point,
            occupiedIds.Contains(point.Id),
            runtimeStatus.GetFaulted(point.Id) is not false,
            point.IsDisabled,
            point.IsLocked)).ToArray();
    }
}
