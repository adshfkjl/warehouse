using Warehouse.Wms.Application.Outbound;
using Warehouse.Wms.Domain.MasterData;

namespace Warehouse.Wms.UnitTests.Outbound;

public sealed class LoadingPointCatalogTests
{
    [Fact]
    public async Task In_memory_catalog_returns_explicit_points_and_honors_cancellation()
    {
        var point = new OutboundLoadingPoint(new LoadingPoint(Guid.NewGuid(), "LP-01", "测试装载点"), false);
        var catalog = new InMemoryLoadingPointCatalog([point]);

        var result = await catalog.GetAsync();

        Assert.Single(result);
        Assert.Equal(point.LoadingPoint.Id, result[0].LoadingPoint.Id);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => catalog.GetAsync(cancellation.Token));
    }
}
