using Microsoft.EntityFrameworkCore;
using Warehouse.Wms.Application.Points;
using Warehouse.Wms.Infrastructure.Persistence;

namespace Warehouse.Wms.Infrastructure.Warehouse;

public sealed class SqlServerPointReadModel(IDbContextFactory<WarehouseDbContext> factory, TimeSpan? freshnessThreshold = null) : IPointReadModel
{
    private readonly TimeSpan _threshold = freshnessThreshold ?? TimeSpan.FromMinutes(2);
    public async Task UpsertAsync(WarehousePointSnapshot snapshot, CancellationToken cancellationToken = default)
    { await using var db=await factory.CreateDbContextAsync(cancellationToken); var current=await db.PointSnapshots.SingleOrDefaultAsync(x=>x.LocationCode==snapshot.LocationCode && x.SourceVersion==snapshot.SourceVersion,cancellationToken); if(current is null) db.PointSnapshots.Add(ToEntity(snapshot)); else Apply(current,snapshot); await db.SaveChangesAsync(cancellationToken); }
    public IReadOnlyCollection<WarehousePointSnapshot> Query(PointQuery? query = null, DateTimeOffset? now = null)
    { query ??= new PointQuery(); var current=now??DateTimeOffset.UtcNow; using var db=factory.CreateDbContext(); var rows=db.PointSnapshots.AsNoTracking().GroupBy(x=>x.LocationCode).Select(g=>g.OrderByDescending(x=>x.SourceVersion).First()).ToList(); return rows.Select(x=>FromEntity(x,current)).Where(p=>Matches(p,query)).ToArray(); }
    public WarehousePointSnapshot? GetByLocation(string locationCode, DateTimeOffset? now = null) => string.IsNullOrWhiteSpace(locationCode)?null:Query(new PointQuery{LocationCode=locationCode.Trim()},now).FirstOrDefault();
    public Task<IReadOnlyCollection<WarehousePointSnapshot>> QueryAsync(PointQuery? query = null, DateTimeOffset? now = null, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(Query(query, now)); }
    public PalletPosition? FindPallet(string palletCode, DateTimeOffset? now = null) { if(string.IsNullOrWhiteSpace(palletCode)) return null; var p=Query(new PointQuery{PalletCode=palletCode.Trim()},now).FirstOrDefault(); return p?.PalletCode is null?null:new PalletPosition(p.PalletCode,p.LocationCode,p.MaterialCode,p.Quantity,p.WeightKg,p.Status,p.ObservedAt,p.Freshness); }
    private WarehousePointSnapshot FromEntity(PointSnapshotEntity x,DateTimeOffset now)=>new(x.WarehouseCode,x.ZoneCode,x.Aisle,x.Rack,x.Level,x.LocationCode,x.Status,x.PalletCode,x.MaterialCode,x.MaterialName,x.BatchNumber,x.Quantity,x.WeightKg,x.ObservedAt,x.SourceVersion,x.IsLocked,x.LockReason,x.TaskState,x.LoadPoint,x.Status=="Offline"?"Offline":now-x.ObservedAt>_threshold?"Stale":"Fresh");
    private static PointSnapshotEntity ToEntity(WarehousePointSnapshot x)=>new(){Id=Guid.NewGuid(),WarehouseCode=x.WarehouseCode,ZoneCode=x.ZoneCode,Aisle=x.Aisle,Rack=x.Rack,Level=x.Level,LocationCode=x.LocationCode,Status=x.Status,PalletCode=x.PalletCode,MaterialCode=x.MaterialCode,MaterialName=x.MaterialName,BatchNumber=x.BatchNumber,Quantity=x.Quantity,WeightKg=x.WeightKg,ObservedAt=x.ObservedAt,SourceVersion=x.SourceVersion,IsLocked=x.IsLocked,LockReason=x.LockReason,TaskState=x.TaskState,LoadPoint=x.LoadPoint};
    private static void Apply(PointSnapshotEntity e,WarehousePointSnapshot x){e.Status=x.Status;e.PalletCode=x.PalletCode;e.MaterialCode=x.MaterialCode;e.MaterialName=x.MaterialName;e.BatchNumber=x.BatchNumber;e.Quantity=x.Quantity;e.WeightKg=x.WeightKg;e.ObservedAt=x.ObservedAt;e.IsLocked=x.IsLocked;e.LockReason=x.LockReason;e.TaskState=x.TaskState;e.LoadPoint=x.LoadPoint;}
    private static bool Matches(WarehousePointSnapshot p,PointQuery q)=>(q.WarehouseCode is null||p.WarehouseCode.Equals(q.WarehouseCode,StringComparison.OrdinalIgnoreCase))&&(q.ZoneCode is null||p.ZoneCode.Equals(q.ZoneCode,StringComparison.OrdinalIgnoreCase))&&(q.Aisle is null||p.Aisle.Equals(q.Aisle,StringComparison.OrdinalIgnoreCase))&&(q.Rack is null||p.Rack.Equals(q.Rack,StringComparison.OrdinalIgnoreCase))&&(q.Level is null||p.Level==q.Level)&&(q.LocationCode is null||p.LocationCode.Equals(q.LocationCode,StringComparison.OrdinalIgnoreCase))&&(q.Status is null||p.Status.Equals(q.Status,StringComparison.OrdinalIgnoreCase))&&(q.MaterialCode is null||p.MaterialCode?.Equals(q.MaterialCode,StringComparison.OrdinalIgnoreCase)==true)&&(q.PalletCode is null||p.PalletCode?.Equals(q.PalletCode,StringComparison.OrdinalIgnoreCase)==true);
}
