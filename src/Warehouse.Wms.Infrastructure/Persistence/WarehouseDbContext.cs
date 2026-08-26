using Microsoft.EntityFrameworkCore;
using Warehouse.Wms.Domain.Inventory;
using Warehouse.Wms.Domain.MasterData;
using Warehouse.Wms.Domain.Tasks;
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
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();
    public DbSet<WarehouseTask> Tasks => Set<WarehouseTask>();
    public DbSet<TaskStateHistory> TaskStateHistories => Set<TaskStateHistory>();
    public DbSet<ResourceLock> ResourceLocks => Set<ResourceLock>();
    public DbSet<TaskIdempotencyKey> TaskIdempotencyKeys => Set<TaskIdempotencyKey>();
    public DbSet<BusinessWorkflowEntity> BusinessWorkflows => Set<BusinessWorkflowEntity>();
    public DbSet<BusinessWorkflowStateHistoryEntity> BusinessWorkflowHistories => Set<BusinessWorkflowStateHistoryEntity>();
    public DbSet<BusinessWorkflowIdempotencyEntity> BusinessWorkflowIdempotency => Set<BusinessWorkflowIdempotencyEntity>();
    public DbSet<StatisticsBatchEntity> StatisticsBatches => Set<StatisticsBatchEntity>();
    public DbSet<StatisticsTrendEntity> StatisticsTrends => Set<StatisticsTrendEntity>();
    public DbSet<StatisticsTaskStateEntity> StatisticsTaskStates => Set<StatisticsTaskStateEntity>();
    public DbSet<PointSnapshotEntity> PointSnapshots => Set<PointSnapshotEntity>();
    public DbSet<IdentityUserEntity> IdentityUsers => Set<IdentityUserEntity>();
    public DbSet<IdentityRoleEntity> IdentityRoles => Set<IdentityRoleEntity>();
    public DbSet<IdentityUserRoleEntity> IdentityUserRoles => Set<IdentityUserRoleEntity>();
    public DbSet<IdentityRolePermissionEntity> IdentityRolePermissions => Set<IdentityRolePermissionEntity>();
    public DbSet<IdentityWarehouseScopeEntity> IdentityWarehouseScopes => Set<IdentityWarehouseScopeEntity>();
    public DbSet<IdentityRefreshTokenEntity> IdentityRefreshTokens => Set<IdentityRefreshTokenEntity>();
    public DbSet<IdentityAuditEntity> IdentityAudits => Set<IdentityAuditEntity>();
    public DbSet<IdentityBootstrapMarkerEntity> IdentityBootstrapMarkers => Set<IdentityBootstrapMarkerEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<IdentityUserEntity>(entity => { entity.ToTable("IdentityUsers"); entity.HasKey(x => x.Id); entity.Property(x => x.UserId).HasMaxLength(128).IsRequired(); entity.Property(x => x.NormalizedUserId).HasMaxLength(128).IsRequired(); entity.Property(x => x.DisplayName).HasMaxLength(200).IsRequired(); entity.Property(x => x.PasswordHash).HasMaxLength(512).IsRequired(); entity.Property(x => x.Version).IsRowVersion(); entity.HasIndex(x => x.NormalizedUserId).IsUnique(); });
        modelBuilder.Entity<IdentityRoleEntity>(entity => { entity.ToTable("IdentityRoles"); entity.HasKey(x => x.Id); entity.Property(x => x.Name).HasMaxLength(128).IsRequired(); entity.Property(x => x.NormalizedName).HasMaxLength(128).IsRequired(); entity.HasIndex(x => x.NormalizedName).IsUnique(); });
        modelBuilder.Entity<IdentityUserRoleEntity>(entity => { entity.ToTable("IdentityUserRoles"); entity.HasKey(x => new { x.UserId, x.RoleId }); entity.HasOne<IdentityUserEntity>().WithMany(x => x.Roles).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade); entity.HasOne<IdentityRoleEntity>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade); });
        modelBuilder.Entity<IdentityRolePermissionEntity>(entity => { entity.ToTable("IdentityRolePermissions"); entity.HasKey(x => x.Id); entity.Property(x => x.Permission).HasMaxLength(256).IsRequired(); entity.Property(x => x.NormalizedPermission).HasMaxLength(256).IsRequired(); entity.HasIndex(x => new { x.RoleId, x.NormalizedPermission }).IsUnique(); entity.HasOne<IdentityRoleEntity>().WithMany(x => x.Permissions).HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade); });
        modelBuilder.Entity<IdentityWarehouseScopeEntity>(entity => { entity.ToTable("IdentityWarehouseScopes"); entity.HasKey(x => x.Id); entity.Property(x => x.WarehouseId).HasMaxLength(128).IsRequired(); entity.Property(x => x.NormalizedWarehouseId).HasMaxLength(128).IsRequired(); entity.HasIndex(x => new { x.UserId, x.NormalizedWarehouseId }).IsUnique(); entity.HasOne<IdentityUserEntity>().WithMany(x => x.WarehouseScopes).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade); });
        modelBuilder.Entity<IdentityRefreshTokenEntity>(entity => { entity.ToTable("IdentityRefreshTokens"); entity.HasKey(x => x.Id); entity.Property(x => x.TokenHash).HasMaxLength(128).IsRequired(); entity.Property(x => x.Version).IsRowVersion(); entity.HasIndex(x => x.TokenHash).IsUnique(); entity.HasIndex(x => new { x.FamilyId, x.RevokedAt }); entity.HasIndex(x => new { x.UserId, x.RevokedAt, x.ExpiresAt }); entity.HasOne<IdentityUserEntity>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade); });
        modelBuilder.Entity<IdentityAuditEntity>(entity => { entity.ToTable("IdentityAudits"); entity.HasKey(x => x.Id); entity.Property(x => x.Action).HasConversion<string>().HasMaxLength(64).IsRequired(); entity.Property(x => x.UserId).HasMaxLength(128).IsRequired(); entity.Property(x => x.Target).HasMaxLength(256).IsRequired(); entity.Property(x => x.Reason).HasMaxLength(1000).IsRequired(); entity.Property(x => x.CorrelationId).HasMaxLength(64).IsRequired(); entity.HasIndex(x => new { x.OccurredAt, x.Id }); entity.HasIndex(x => new { x.UserId, x.OccurredAt }); });
        modelBuilder.Entity<IdentityBootstrapMarkerEntity>(entity => { entity.ToTable("IdentityBootstrapMarkers"); entity.HasKey(x => x.Name); entity.Property(x => x.Name).HasMaxLength(64); });

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

        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("OutboxMessages");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.MessageType).HasMaxLength(128).IsRequired();
            entity.Property(x => x.AggregateType).HasMaxLength(128).IsRequired();
            entity.Property(x => x.AggregateId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Payload).HasColumnType("nvarchar(max)").IsRequired();
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.Property(x => x.ClaimedBy).HasMaxLength(128);
            entity.Property(x => x.LastError).HasMaxLength(1000);
            entity.HasIndex(x => x.IdempotencyKey).IsUnique();
            entity.HasIndex(x => new { x.Status, x.NextAttemptAt, x.ClaimExpiresAt });
            entity.HasIndex(x => new { x.AggregateType, x.AggregateId });
        });

        modelBuilder.Entity<InboxMessage>(entity =>
        {
            entity.ToTable("InboxMessages");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.MessageId).HasMaxLength(200).IsRequired();
            entity.Property(x => x.MessageType).HasMaxLength(128).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Payload).HasColumnType("nvarchar(max)").IsRequired();
            entity.Property(x => x.Source).HasMaxLength(64);
            entity.Property(x => x.ResultVersion).IsRequired();
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.Property(x => x.ClaimedBy).HasMaxLength(128);
            entity.Property(x => x.LastError).HasMaxLength(1000);
            entity.HasIndex(x => x.MessageId).IsUnique();
            entity.HasIndex(x => new { x.IdempotencyKey, x.ResultVersion }).IsUnique();
            entity.HasIndex(x => new { x.Status, x.ReceivedAt, x.ClaimExpiresAt });
        });

        modelBuilder.Entity<WarehouseTask>(entity =>
        {
            entity.ToTable("WarehouseTasks");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.TaskNumber).HasMaxLength(128).IsRequired();
            entity.Property(x => x.TaskType).HasMaxLength(64).IsRequired();
            entity.Property(x => x.State).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(x => x.DispatchContextJson).HasColumnType("nvarchar(max)");
            entity.Property(x => x.WorkflowKind).HasMaxLength(64);
            entity.Property(x => x.WorkflowReference).HasMaxLength(256);
            entity.Property(x => x.WorkflowSnapshotJson).HasColumnType("nvarchar(max)");
            entity.Property(x => x.WorkflowRecoveryStatus).HasConversion<string>().HasMaxLength(64).IsRequired();
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => x.TaskNumber).IsUnique();
            entity.HasMany(x => x.StateHistory).WithOne().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TaskStateHistory>(entity =>
        {
            entity.ToTable("TaskStateHistories");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.FromState).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(x => x.ToState).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(x => x.Operator).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Reason).HasMaxLength(500).IsRequired();
            entity.Property(x => x.ErrorCode).HasMaxLength(128);
            entity.HasIndex(x => new { x.TaskId, x.Version }).IsUnique();
            entity.HasOne<WarehouseTask>().WithMany(x => x.StateHistory).HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ResourceLock>(entity =>
        {
            entity.ToTable("ResourceLocks");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ResourceType).HasMaxLength(64).IsRequired();
            entity.Property(x => x.ResourceId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.OwnerTaskNumber).HasMaxLength(128).IsRequired();
            entity.Property(x => x.LockToken).IsRequired();
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.ResourceType, x.ResourceId }).IsUnique().HasFilter("[ReleasedAt] IS NULL");
            entity.HasIndex(x => new { x.ExpiresAt, x.ReleasedAt });
        });

        modelBuilder.Entity<TaskIdempotencyKey>(entity =>
        {
            entity.ToTable("TaskIdempotencyKeys");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Scope).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Key).HasMaxLength(200).IsRequired();
            entity.Property(x => x.RequestHash).HasMaxLength(128).IsRequired();
            entity.HasIndex(x => new { x.Scope, x.Key }).IsUnique();
            entity.HasIndex(x => x.TaskId);
        });

        modelBuilder.Entity<BusinessWorkflowEntity>(entity =>
        {
            entity.ToTable("BusinessWorkflows");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.AggregateType).HasMaxLength(128).IsRequired();
            entity.Property(x => x.AggregateKey).HasMaxLength(256).IsRequired();
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.Property(x => x.Status).HasMaxLength(64).IsRequired();
            entity.Property(x => x.SnapshotJson).HasColumnType("nvarchar(max)").IsRequired();
            entity.Property(x => x.WarehouseTaskNumber).HasMaxLength(128);
            entity.HasIndex(x => new { x.AggregateType, x.AggregateKey }).IsUnique();
            entity.HasIndex(x => x.UpdatedAt);
        });

        modelBuilder.Entity<BusinessWorkflowStateHistoryEntity>(entity =>
        {
            entity.ToTable("BusinessWorkflowStateHistories");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.AggregateType).HasMaxLength(128).IsRequired();
            entity.Property(x => x.AggregateKey).HasMaxLength(256).IsRequired();
            entity.Property(x => x.FromStatus).HasMaxLength(64);
            entity.Property(x => x.ToStatus).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Reason).HasMaxLength(500);
            entity.Property(x => x.OperatorId).HasMaxLength(128);
            entity.HasIndex(x => new { x.AggregateType, x.AggregateKey, x.Version }).IsUnique();
        });

        modelBuilder.Entity<BusinessWorkflowIdempotencyEntity>(entity =>
        {
            entity.ToTable("BusinessWorkflowIdempotency");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Scope).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Key).HasMaxLength(256).IsRequired();
            entity.Property(x => x.RequestHash).HasMaxLength(128).IsRequired();
            entity.Property(x => x.AggregateType).HasMaxLength(128);
            entity.Property(x => x.AggregateKey).HasMaxLength(256);
            entity.HasIndex(x => new { x.Scope, x.Key }).IsUnique();
        });

        modelBuilder.Entity<StatisticsBatchEntity>(entity =>
        {
            entity.ToTable("StatisticsBatches"); entity.HasKey(x => x.Id); entity.Property(x => x.BatchId).HasMaxLength(128).IsRequired(); entity.Property(x => x.Period).HasMaxLength(16).IsRequired(); entity.Property(x => x.SourceVersion).HasMaxLength(128).IsRequired(); entity.Property(x => x.WarehouseCode).HasMaxLength(64); entity.Property(x => x.Freshness).HasMaxLength(32).IsRequired();
            entity.Property(x => x.InventoryQuantity).HasPrecision(18, 3); entity.Property(x => x.InventoryWeightKg).HasPrecision(18, 3); entity.Property(x => x.LocationUtilizationPercent).HasPrecision(18, 3); entity.Property(x => x.InboundQuantity).HasPrecision(18, 3); entity.Property(x => x.OutboundQuantity).HasPrecision(18, 3); entity.Property(x => x.TransferQuantity).HasPrecision(18, 3); entity.Property(x => x.TaskSuccessRatePercent).HasPrecision(18, 3);
            entity.HasIndex(x => new { x.Period, x.PeriodStart, x.PeriodEnd, x.WarehouseCode }).IsUnique(); entity.HasIndex(x => x.BatchId).IsUnique(); entity.HasMany(x => x.Trends).WithOne().HasForeignKey(x => x.BatchEntityId).OnDelete(DeleteBehavior.Cascade); entity.HasMany(x => x.TaskStates).WithOne().HasForeignKey(x => x.BatchEntityId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<StatisticsTrendEntity>(entity => { entity.ToTable("StatisticsTrends"); entity.HasKey(x => x.Id); entity.Property(x => x.InboundQuantity).HasPrecision(18, 3); entity.Property(x => x.OutboundQuantity).HasPrecision(18, 3); entity.Property(x => x.TransferQuantity).HasPrecision(18, 3); });
        modelBuilder.Entity<StatisticsTaskStateEntity>(entity => { entity.ToTable("StatisticsTaskStates"); entity.HasKey(x => x.Id); entity.Property(x => x.State).HasMaxLength(64).IsRequired(); });
        modelBuilder.Entity<PointSnapshotEntity>(entity => { entity.ToTable("PointSnapshots"); entity.HasKey(x => x.Id); entity.Property(x => x.LocationCode).HasMaxLength(64).IsRequired(); entity.Property(x => x.WarehouseCode).HasMaxLength(64).IsRequired(); entity.Property(x => x.Status).HasMaxLength(32).IsRequired(); entity.HasIndex(x => new { x.LocationCode, x.SourceVersion }).IsUnique(); entity.HasIndex(x => x.LocationCode); entity.Property(x => x.Quantity).HasPrecision(18, 3); entity.Property(x => x.WeightKg).HasPrecision(18, 3); });

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
