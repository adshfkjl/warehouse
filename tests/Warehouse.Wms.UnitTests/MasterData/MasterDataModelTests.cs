using Microsoft.EntityFrameworkCore;
using Warehouse.Wms.Domain.MasterData;
using Warehouse.Wms.Infrastructure.Persistence;
using WarehouseEntity = Warehouse.Wms.Domain.MasterData.Warehouse;

namespace Warehouse.Wms.UnitTests.MasterData;

public sealed class MasterDataModelTests
{
    [Fact]
    public void Location_rejects_non_positive_capacity_and_dimensions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Location(
            Guid.NewGuid(), "A-001", 0, 1000m, 1000m, 1000m, 1000m));

        Assert.Throws<ArgumentOutOfRangeException>(() => new Location(
            Guid.NewGuid(), "A-001", 1, 0m, 1000m, 1000m, 1000m));
    }

    [Fact]
    public void Master_data_codes_are_trimmed_and_required()
    {
        var warehouse = new WarehouseEntity(" WH-01 ", "Main warehouse");
        var pallet = new Pallet(" P-001 ");

        Assert.Equal("WH-01", warehouse.Code);
        Assert.Equal("P-001", pallet.Code);
        Assert.Throws<ArgumentException>(() => new Material(" ", "Material"));
        Assert.Throws<ArgumentException>(() => new LoadingPoint(" ", "LP"));
    }

    [Fact]
    public void Model_has_unique_business_keys_and_pallet_occupancy_constraint()
    {
        using var context = new WarehouseDbContext(
            new DbContextOptionsBuilder<WarehouseDbContext>()
                .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=wms-model-test")
                .Options);

        var model = context.Model;
        AssertUniqueIndex(model, typeof(Location), nameof(Location.Code));
        AssertUniqueIndex(model, typeof(Pallet), nameof(Pallet.Code));
        AssertUniqueIndex(model, typeof(Equipment), nameof(Equipment.EquipmentNumber));
        AssertUniqueIndex(model, typeof(LoadingPoint), nameof(LoadingPoint.Code));

        var occupancyIndex = model.FindEntityType(typeof(Pallet))!
            .GetIndexes()
            .Single(index => index.Properties.Single().Name == nameof(Pallet.CurrentLocationId));
        Assert.True(occupancyIndex.IsUnique);
        Assert.Equal("[CurrentLocationId] IS NOT NULL", occupancyIndex.GetFilter());

        var loadingPointIndex = model.FindEntityType(typeof(Pallet))!
            .GetIndexes()
            .Single(index => index.Properties.Single().Name == nameof(Pallet.CurrentLoadingPointId));
        Assert.True(loadingPointIndex.IsUnique);
        Assert.Equal("[CurrentLoadingPointId] IS NOT NULL", loadingPointIndex.GetFilter());
    }

    [Fact]
    public void Master_data_model_is_sql_server_independent_of_erp()
    {
        using var context = new WarehouseDbContext(
            new DbContextOptionsBuilder<WarehouseDbContext>()
                .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=wms-model-test")
                .Options);

        Assert.DoesNotContain(context.Model.GetEntityTypes(), entity =>
            entity.GetTableName()?.Contains("ERP", StringComparison.OrdinalIgnoreCase) == true);
    }

    private static void AssertUniqueIndex(Microsoft.EntityFrameworkCore.Metadata.IReadOnlyModel model, Type entityType, string property)
    {
        var index = model.FindEntityType(entityType)!.GetIndexes()
            .Single(index => index.Properties.Single().Name == property);
        Assert.True(index.IsUnique);
    }
}
