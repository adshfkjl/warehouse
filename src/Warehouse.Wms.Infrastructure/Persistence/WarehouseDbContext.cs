using Microsoft.EntityFrameworkCore;
using Warehouse.Wms.Domain.Inventory;
using Warehouse.Wms.Domain.MasterData;
using WarehouseEntity = Warehouse.Wms.Domain.MasterData.Warehouse;

namespace Warehouse.Wms.Infrastructure.Persistence;

public sealed class WarehouseDbContext(DbContextOptions<WarehouseDbContext> options) : DbContext(options)
{
    public DbSet<WarehouseEntity> Warehouses => Set<WarehouseEntity>();
    public DbSet<Zone> Zones => Set<Zone>();
    public DbSet<Aisle> Aisles => Set<Aisle>();
    public DbSet<Rack> Racks => Set<Rack>();
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<LoadingPoint> LoadingPoints => Set<LoadingPoint>();
    public DbSet<Equipment> Equipment => Set<Equipment>();
    public DbSet<Material> Materials => Set<Material>();
    public DbSet<Container> Containers => Set<Container>();
    public DbSet<Pallet> Pallets => Set<Pallet>();
    public DbSet<InventoryBalanceEntity> InventoryBalances => Set<InventoryBalanceEntity>();
    public DbSet<InventoryTransactionEntity> InventoryTransactions => Set<InventoryTransactionEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<WarehouseEntity>(entity =>
        {
            entity.ToTable("Warehouses");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Code).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.FieldMapping).HasMaxLength(200);
            entity.HasIndex(x => x.Code).IsUnique();
        });

        modelBuilder.Entity<Zone>(entity =>
        {
            entity.ToTable("Zones");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Code).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.HasIndex(x => new { x.WarehouseId, x.Code }).IsUnique();
            entity.HasOne<WarehouseEntity>().WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Aisle>(entity =>
        {
            entity.ToTable("Aisles");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Code).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.HasIndex(x => new { x.ZoneId, x.Code }).IsUnique();
            entity.HasOne<Zone>().WithMany().HasForeignKey(x => x.ZoneId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Rack>(entity =>
        {
            entity.ToTable("Racks");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Code).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.HasIndex(x => new { x.AisleId, x.Code }).IsUnique();
            entity.HasOne<Aisle>().WithMany().HasForeignKey(x => x.AisleId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Location>(entity =>
        {
            entity.ToTable("Locations", table => table.HasCheckConstraint("CK_Locations_PositiveDimensions", "Capacity > 0 AND MaxWeightKg > 0 AND LengthMm > 0 AND WidthMm > 0 AND HeightMm > 0"));
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Code).HasMaxLength(64).IsRequired();
            entity.Property(x => x.MaxWeightKg).HasPrecision(18, 3);
            entity.Property(x => x.LengthMm).HasPrecision(18, 3);
            entity.Property(x => x.WidthMm).HasPrecision(18, 3);
            entity.Property(x => x.HeightMm).HasPrecision(18, 3);
            entity.Property(x => x.FieldMapping).HasMaxLength(200);
            entity.HasIndex(x => x.Code).IsUnique();
            entity.HasOne<Rack>().WithMany().HasForeignKey(x => x.RackId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LoadingPoint>(entity =>
        {
            entity.ToTable("LoadingPoints");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Code).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.FieldMapping).HasMaxLength(200);
            entity.HasIndex(x => x.Code).IsUnique();
        });

        modelBuilder.Entity<Equipment>(entity =>
        {
            entity.ToTable("Equipment");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EquipmentNumber).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Type).HasMaxLength(64).IsRequired();
            entity.Property(x => x.PlcId).HasMaxLength(64);
            entity.Property(x => x.FieldMapping).HasMaxLength(200);
            entity.HasIndex(x => x.EquipmentNumber).IsUnique();
        });

        modelBuilder.Entity<Material>(entity =>
        {
            entity.ToTable("Materials");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Code).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.UnitWeightKg).HasPrecision(18, 3);
            entity.HasIndex(x => x.Code).IsUnique();
        });

        modelBuilder.Entity<Container>(entity =>
        {
            entity.ToTable("Containers");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Code).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Type).HasMaxLength(64).IsRequired();
            entity.HasIndex(x => x.Code).IsUnique();
        });

        modelBuilder.Entity<Pallet>(entity =>
        {
            entity.ToTable("Pallets", table => table.HasCheckConstraint("CK_Pallets_OneCurrentOwner", "NOT (CurrentLocationId IS NOT NULL AND CurrentLoadingPointId IS NOT NULL)"));
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Code).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Type).HasMaxLength(64);
            entity.Property(x => x.OwnershipStatus).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => x.Code).IsUnique();
            entity.HasIndex(x => x.CurrentLoadingPointId).IsUnique().HasFilter("[CurrentLoadingPointId] IS NOT NULL");
            entity.HasIndex(x => x.CurrentLocationId).IsUnique().HasFilter("[CurrentLocationId] IS NOT NULL");
            entity.HasOne<Location>().WithMany().HasForeignKey(x => x.CurrentLocationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<LoadingPoint>().WithMany().HasForeignKey(x => x.CurrentLoadingPointId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<InventoryBalanceEntity>(entity =>
        {
            entity.ToTable("InventoryBalances", table =>
                table.HasCheckConstraint("CK_InventoryBalances_NonNegative", "Quantity >= 0 AND WeightKg >= 0"));
            entity.HasKey(x => x.Id);
            entity.Property(x => x.BatchNumber).HasMaxLength(128);
            entity.Property(x => x.Quantity).HasPrecision(18, 3);
            entity.Property(x => x.WeightKg).HasPrecision(18, 3);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.Property(x => x.BalanceKey).HasMaxLength(500).IsRequired();
            entity.HasIndex(x => x.BalanceKey).IsUnique();
            entity.HasIndex(x => new { x.MaterialId, x.LocationId });
        });

        modelBuilder.Entity<InventoryTransactionEntity>(entity =>
        {
            entity.ToTable("InventoryTransactions", table =>
                table.HasCheckConstraint(
                    "CK_InventoryTransactions_QuantityWeight",
                    "Type = 'Adjustment' OR (Quantity >= 0 AND WeightKg >= 0)"));
            entity.HasKey(x => x.Id);
            entity.Property(x => x.IdempotencyKey).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Fingerprint).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Type).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(x => x.StatusBefore).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(x => x.StatusAfter).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(x => x.BatchNumber).HasMaxLength(128);
            entity.Property(x => x.SourceDocumentId).HasMaxLength(128);
            entity.Property(x => x.TaskNumber).HasMaxLength(128);
            entity.Property(x => x.OperatorId).HasMaxLength(128);
            entity.Property(x => x.Reason).HasMaxLength(500);
            entity.Property(x => x.Quantity).HasPrecision(18, 3);
            entity.Property(x => x.WeightKg).HasPrecision(18, 3);
            entity.HasIndex(x => x.IdempotencyKey).IsUnique();
            entity.HasIndex(x => new { x.MaterialId, x.OccurredAt });
        });

        SeedDevelopmentData(modelBuilder);
    }

    private static void SeedDevelopmentData(ModelBuilder modelBuilder)
    {
        var warehouseId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var zoneId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var aisleId = Guid.Parse("00000000-0000-0000-0000-000000000003");
        var rackId = Guid.Parse("00000000-0000-0000-0000-000000000004");
        var locationId = Guid.Parse("00000000-0000-0000-0000-000000000005");
        var loadingPointId = Guid.Parse("00000000-0000-0000-0000-000000000006");
        var equipmentId = Guid.Parse("00000000-0000-0000-0000-000000000007");

        modelBuilder.Entity<WarehouseEntity>().HasData(new { Id = warehouseId, Code = "DEV", Name = "开发仓库", IsDisabled = false });
        modelBuilder.Entity<Zone>().HasData(new { Id = zoneId, WarehouseId = warehouseId, Code = "Z01", Name = "开发库区", IsDisabled = false });
        modelBuilder.Entity<Aisle>().HasData(new { Id = aisleId, ZoneId = zoneId, Code = "A01", Name = "开发巷道", IsDisabled = false });
        modelBuilder.Entity<Rack>().HasData(new { Id = rackId, AisleId = aisleId, Code = "R01", Name = "开发货架", IsDisabled = false });
        modelBuilder.Entity<Location>().HasData(new
        {
            Id = locationId, RackId = rackId, Code = "DEV-A01-R01-001", Capacity = 1,
            MaxWeightKg = 1000m, LengthMm = 1200m, WidthMm = 1000m, HeightMm = 1500m,
            IsDisabled = false, IsLocked = false
        });
        modelBuilder.Entity<LoadingPoint>().HasData(new { Id = loadingPointId, Code = "LP-DEV-01", Name = "开发装载点", IsDisabled = false, IsLocked = false });
        modelBuilder.Entity<Equipment>().HasData(new { Id = equipmentId, EquipmentNumber = "DEV-EQ-01", Name = "模拟堆垛机", Type = "Simulator", IsDisabled = false });
    }
}
