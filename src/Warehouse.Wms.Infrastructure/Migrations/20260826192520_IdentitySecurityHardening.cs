using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Warehouse.Wms.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class IdentitySecurityHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_IdentityWarehouseScopes_UserId_WarehouseId",
                table: "IdentityWarehouseScopes");

            migrationBuilder.DropIndex(
                name: "IX_IdentityUsers_UserId",
                table: "IdentityUsers");

            migrationBuilder.DropIndex(
                name: "IX_IdentityRoles_Name",
                table: "IdentityRoles");

            migrationBuilder.DropIndex(
                name: "IX_IdentityRolePermissions_RoleId_Permission",
                table: "IdentityRolePermissions");

            migrationBuilder.AddColumn<string>(
                name: "NormalizedWarehouseId",
                table: "IdentityWarehouseScopes",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "FailedLoginCount",
                table: "IdentityUsers",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FirstFailedLoginAt",
                table: "IdentityUsers",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LockedUntil",
                table: "IdentityUsers",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedUserId",
                table: "IdentityUsers",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "SecurityVersion",
                table: "IdentityUsers",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<byte[]>(
                name: "Version",
                table: "IdentityUsers",
                type: "rowversion",
                rowVersion: true,
                nullable: false);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedName",
                table: "IdentityRoles",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "NormalizedPermission",
                table: "IdentityRolePermissions",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "FamilyId",
                table: "IdentityRefreshTokens",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "ParentTokenId",
                table: "IdentityRefreshTokens",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReplacedByTokenId",
                table: "IdentityRefreshTokens",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReuseDetectedAt",
                table: "IdentityRefreshTokens",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CorrelationId",
                table: "IdentityAudits",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "IdentityBootstrapMarkers",
                columns: table => new
                {
                    Name = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityBootstrapMarkers", x => x.Name);
                });

            migrationBuilder.Sql("UPDATE dbo.IdentityUsers SET NormalizedUserId = UPPER(LTRIM(RTRIM(UserId))), SecurityVersion = CASE WHEN SecurityVersion = 0 THEN 1 ELSE SecurityVersion END;");
            migrationBuilder.Sql("UPDATE dbo.IdentityRoles SET NormalizedName = UPPER(LTRIM(RTRIM(Name)));");
            migrationBuilder.Sql("UPDATE dbo.IdentityRolePermissions SET NormalizedPermission = UPPER(LTRIM(RTRIM(Permission)));");
            migrationBuilder.Sql("UPDATE dbo.IdentityWarehouseScopes SET NormalizedWarehouseId = UPPER(LTRIM(RTRIM(WarehouseId)));");
            migrationBuilder.Sql("UPDATE dbo.IdentityAudits SET CorrelationId = CONCAT('legacy-', CONVERT(varchar(36), Id)) WHERE CorrelationId = '';");
            migrationBuilder.Sql("CREATE TRIGGER dbo.TR_IdentityAudits_AppendOnly ON dbo.IdentityAudits AFTER UPDATE, DELETE AS BEGIN THROW 51000, 'Identity audit records are append-only.', 1; END;");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityWarehouseScopes_UserId_NormalizedWarehouseId",
                table: "IdentityWarehouseScopes",
                columns: new[] { "UserId", "NormalizedWarehouseId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IdentityUsers_NormalizedUserId",
                table: "IdentityUsers",
                column: "NormalizedUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IdentityRoles_NormalizedName",
                table: "IdentityRoles",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IdentityRolePermissions_RoleId_NormalizedPermission",
                table: "IdentityRolePermissions",
                columns: new[] { "RoleId", "NormalizedPermission" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IdentityRefreshTokens_FamilyId_RevokedAt",
                table: "IdentityRefreshTokens",
                columns: new[] { "FamilyId", "RevokedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_IdentityAudits_OccurredAt_Id",
                table: "IdentityAudits",
                columns: new[] { "OccurredAt", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS dbo.TR_IdentityAudits_AppendOnly;");
            migrationBuilder.DropTable(
                name: "IdentityBootstrapMarkers");

            migrationBuilder.DropIndex(
                name: "IX_IdentityWarehouseScopes_UserId_NormalizedWarehouseId",
                table: "IdentityWarehouseScopes");

            migrationBuilder.DropIndex(
                name: "IX_IdentityUsers_NormalizedUserId",
                table: "IdentityUsers");

            migrationBuilder.DropIndex(
                name: "IX_IdentityRoles_NormalizedName",
                table: "IdentityRoles");

            migrationBuilder.DropIndex(
                name: "IX_IdentityRolePermissions_RoleId_NormalizedPermission",
                table: "IdentityRolePermissions");

            migrationBuilder.DropIndex(
                name: "IX_IdentityRefreshTokens_FamilyId_RevokedAt",
                table: "IdentityRefreshTokens");

            migrationBuilder.DropIndex(
                name: "IX_IdentityAudits_OccurredAt_Id",
                table: "IdentityAudits");

            migrationBuilder.DropColumn(
                name: "NormalizedWarehouseId",
                table: "IdentityWarehouseScopes");

            migrationBuilder.DropColumn(
                name: "FailedLoginCount",
                table: "IdentityUsers");

            migrationBuilder.DropColumn(
                name: "FirstFailedLoginAt",
                table: "IdentityUsers");

            migrationBuilder.DropColumn(
                name: "LockedUntil",
                table: "IdentityUsers");

            migrationBuilder.DropColumn(
                name: "NormalizedUserId",
                table: "IdentityUsers");

            migrationBuilder.DropColumn(
                name: "SecurityVersion",
                table: "IdentityUsers");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "IdentityUsers");

            migrationBuilder.DropColumn(
                name: "NormalizedName",
                table: "IdentityRoles");

            migrationBuilder.DropColumn(
                name: "NormalizedPermission",
                table: "IdentityRolePermissions");

            migrationBuilder.DropColumn(
                name: "FamilyId",
                table: "IdentityRefreshTokens");

            migrationBuilder.DropColumn(
                name: "ParentTokenId",
                table: "IdentityRefreshTokens");

            migrationBuilder.DropColumn(
                name: "ReplacedByTokenId",
                table: "IdentityRefreshTokens");

            migrationBuilder.DropColumn(
                name: "ReuseDetectedAt",
                table: "IdentityRefreshTokens");

            migrationBuilder.DropColumn(
                name: "CorrelationId",
                table: "IdentityAudits");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityWarehouseScopes_UserId_WarehouseId",
                table: "IdentityWarehouseScopes",
                columns: new[] { "UserId", "WarehouseId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IdentityUsers_UserId",
                table: "IdentityUsers",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IdentityRoles_Name",
                table: "IdentityRoles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IdentityRolePermissions_RoleId_Permission",
                table: "IdentityRolePermissions",
                columns: new[] { "RoleId", "Permission" },
                unique: true);
        }
    }
}
