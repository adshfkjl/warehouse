using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;

#nullable disable

namespace Warehouse.Wms.Infrastructure.Migrations;

[DbContext(typeof(global::Warehouse.Wms.Infrastructure.Persistence.WarehouseDbContext))]
[Migration("20260826191243_IdentityAuditPersistence")]
public partial class IdentityAuditPersistence : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(name: "IdentityAudits", columns: table => new
        {
            Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
            Action = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
            UserId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
            Target = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
            Succeeded = table.Column<bool>(type: "bit", nullable: false),
            Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
            OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_IdentityAudits", x => x.Id));
        migrationBuilder.CreateTable(name: "IdentityRoles", columns: table => new
        {
            Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
            Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
        }, constraints: table => table.PrimaryKey("PK_IdentityRoles", x => x.Id));
        migrationBuilder.CreateTable(name: "IdentityUsers", columns: table => new
        {
            Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
            UserId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
            DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
            PasswordHash = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
            Disabled = table.Column<bool>(type: "bit", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_IdentityUsers", x => x.Id));
        migrationBuilder.CreateTable(name: "IdentityRolePermissions", columns: table => new
        {
            Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false), RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false), Permission = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false)
        }, constraints: table => { table.PrimaryKey("PK_IdentityRolePermissions", x => x.Id); table.ForeignKey("FK_IdentityRolePermissions_IdentityRoles_RoleId", x => x.RoleId, "IdentityRoles", "Id", onDelete: ReferentialAction.Cascade); });
        migrationBuilder.CreateTable(name: "IdentityRefreshTokens", columns: table => new
        {
            Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false), UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false), TokenHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false), ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false), RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true), Version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
        }, constraints: table => { table.PrimaryKey("PK_IdentityRefreshTokens", x => x.Id); table.ForeignKey("FK_IdentityRefreshTokens_IdentityUsers_UserId", x => x.UserId, "IdentityUsers", "Id", onDelete: ReferentialAction.Cascade); });
        migrationBuilder.CreateTable(name: "IdentityUserRoles", columns: table => new
        {
            UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false), RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
        }, constraints: table => { table.PrimaryKey("PK_IdentityUserRoles", x => new { x.UserId, x.RoleId }); table.ForeignKey("FK_IdentityUserRoles_IdentityRoles_RoleId", x => x.RoleId, "IdentityRoles", "Id", onDelete: ReferentialAction.Cascade); table.ForeignKey("FK_IdentityUserRoles_IdentityUsers_UserId", x => x.UserId, "IdentityUsers", "Id", onDelete: ReferentialAction.Cascade); });
        migrationBuilder.CreateTable(name: "IdentityWarehouseScopes", columns: table => new
        {
            Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false), UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false), WarehouseId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false)
        }, constraints: table => { table.PrimaryKey("PK_IdentityWarehouseScopes", x => x.Id); table.ForeignKey("FK_IdentityWarehouseScopes_IdentityUsers_UserId", x => x.UserId, "IdentityUsers", "Id", onDelete: ReferentialAction.Cascade); });
        migrationBuilder.CreateIndex(name: "IX_IdentityAudits_UserId_OccurredAt", table: "IdentityAudits", columns: new[] { "UserId", "OccurredAt" });
        migrationBuilder.CreateIndex(name: "IX_IdentityRefreshTokens_TokenHash", table: "IdentityRefreshTokens", column: "TokenHash", unique: true);
        migrationBuilder.CreateIndex(name: "IX_IdentityRefreshTokens_UserId_RevokedAt_ExpiresAt", table: "IdentityRefreshTokens", columns: new[] { "UserId", "RevokedAt", "ExpiresAt" });
        migrationBuilder.CreateIndex(name: "IX_IdentityRolePermissions_RoleId_Permission", table: "IdentityRolePermissions", columns: new[] { "RoleId", "Permission" }, unique: true);
        migrationBuilder.CreateIndex(name: "IX_IdentityRoles_Name", table: "IdentityRoles", column: "Name", unique: true);
        migrationBuilder.CreateIndex(name: "IX_IdentityUserRoles_RoleId", table: "IdentityUserRoles", column: "RoleId");
        migrationBuilder.CreateIndex(name: "IX_IdentityUsers_UserId", table: "IdentityUsers", column: "UserId", unique: true);
        migrationBuilder.CreateIndex(name: "IX_IdentityWarehouseScopes_UserId_WarehouseId", table: "IdentityWarehouseScopes", columns: new[] { "UserId", "WarehouseId" }, unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "IdentityAudits"); migrationBuilder.DropTable(name: "IdentityRefreshTokens"); migrationBuilder.DropTable(name: "IdentityRolePermissions"); migrationBuilder.DropTable(name: "IdentityUserRoles"); migrationBuilder.DropTable(name: "IdentityWarehouseScopes"); migrationBuilder.DropTable(name: "IdentityRoles"); migrationBuilder.DropTable(name: "IdentityUsers");
    }
}
