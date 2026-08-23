// PLCManagement.API/Data/ApplicationDbContext.cs
using System.Collections.Generic;
using System.Reflection.Emit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using PLCManagement.API.Models;
using PLCManagement.API.Models.Entities;

namespace PLCManagement.API.Data
{
    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
        {
        }

        public DbSet<PlcConfiguration> PlcConfigurations { get; set; }
        public DbSet<LogEntry> LogEntrys { get; set; }
        public DbSet<LocationManagement> LocationManagements { get; set; }
        public DbSet<LocationOperationLog> LocationOperationLogs { get; set; }
        public DbSet<User> Users { get; set; }
        public DbSet<Role> Roles { get; set; }
        public DbSet<Permission> Permissions { get; set; }
        public DbSet<UserRole> UserRoles { get; set; }
        public DbSet<RolePermission> RolePermissions { get; set; }
        public DbSet<RefreshToken> RefreshTokens { get; set; }
        public DbSet<InventoryCheckTask> InventoryCheckTasks { get; set; }
        public DbSet<InventoryCheckItem> InventoryCheckItems { get; set; }

        //protected override void OnModelCreating(ModelBuilder modelBuilder)
        //{
        //    base.OnModelCreating(modelBuilder);

        //    modelBuilder.Entity<PlcConfiguration>()
        //        .HasIndex(p => p.PlcId)
        //        .IsUnique();
        //}

        //protected override void OnModelCreating(ModelBuilder modelBuilder)
        //{
        //    base.OnModelCreating(modelBuilder);

        //    modelBuilder.Entity<PlcConfiguration>()
        //                        .HasIndex(p => p.PlcId)
        //        .IsUnique()

        //        .ToTable(t => t.HasTrigger("trg_PlcConfigurations_Update"))
        //        .Property(p => p.LastTestTime)
        //        .ValueGeneratedOnAddOrUpdate()
        //        .Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Save);
        //}

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Configure the index separately
            modelBuilder.Entity<PlcConfiguration>()
                .HasIndex(p => p.PlcId)
                .IsUnique();

            // Configure the table and properties separately
            modelBuilder.Entity<PlcConfiguration>(entity =>
            {
                entity.ToTable(t => t.HasTrigger("trg_PlcConfigurations_Update"));

                entity.Property(p => p.LastTestTime)
                    .ValueGeneratedOnAddOrUpdate()
                    .Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Save);
            });

            // 配置复合主键
            modelBuilder.Entity<UserRole>()
                .HasKey(ur => new { ur.UserId, ur.RoleId });

            modelBuilder.Entity<RolePermission>()
                .HasKey(rp => new { rp.RoleId, rp.PermissionId });

            // 配置索引
            modelBuilder.Entity<User>()
                .HasIndex(u => u.Username)
                .IsUnique();

            modelBuilder.Entity<Role>()
                .HasIndex(r => r.Name)
                .IsUnique();

            modelBuilder.Entity<Permission>()
                .HasIndex(p => p.Code)
                .IsUnique();

            // 配置关系
            modelBuilder.Entity<UserRole>()
                .HasOne(ur => ur.User)
                .WithMany(u => u.UserRoles)
                .HasForeignKey(ur => ur.UserId);

            modelBuilder.Entity<UserRole>()
                .HasOne(ur => ur.Role)
                .WithMany(r => r.UserRoles)
                .HasForeignKey(ur => ur.RoleId);

            modelBuilder.Entity<RolePermission>()
                .HasOne(rp => rp.Role)
                .WithMany(r => r.RolePermissions)
                .HasForeignKey(rp => rp.RoleId);

            modelBuilder.Entity<RolePermission>()
                .HasOne(rp => rp.Permission)
                .WithMany(p => p.RolePermissions)
                .HasForeignKey(rp => rp.PermissionId);

            modelBuilder.Entity<RefreshToken>()
                .HasOne(rt => rt.User)
                .WithMany(u => u.RefreshTokens)
                .HasForeignKey(rt => rt.UserId);

            modelBuilder.Entity<InventoryCheckTask>(entity =>
            {
                entity.HasIndex(t => t.TaskNo)
                    .IsUnique();

                entity.Property(t => t.TaskNo)
                    .HasMaxLength(50);

                entity.Property(t => t.PLCID)
                    .HasMaxLength(10);

                entity.Property(t => t.TrayStart)
                    .HasMaxLength(50);

                entity.Property(t => t.TrayEnd)
                    .HasMaxLength(50);

                entity.Property(t => t.Status)
                    .HasMaxLength(50);

                entity.Property(t => t.Message)
                    .HasMaxLength(500);
            });

            modelBuilder.Entity<InventoryCheckItem>(entity =>
            {
                entity.HasIndex(i => new { i.TaskId, i.Tray })
                    .IsUnique();

                entity.Property(i => i.PLCID)
                    .HasMaxLength(10);

                entity.Property(i => i.Tray)
                    .HasMaxLength(50);

                entity.Property(i => i.OutboundStatus)
                    .HasMaxLength(50);

                entity.Property(i => i.OutboundMessage)
                    .HasMaxLength(500);

                entity.Property(i => i.InboundStatus)
                    .HasMaxLength(50);

                entity.Property(i => i.InboundMessage)
                    .HasMaxLength(500);

                entity.HasOne(i => i.Task)
                    .WithMany(t => t.Items)
                    .HasForeignKey(i => i.TaskId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

        }
    }
}
