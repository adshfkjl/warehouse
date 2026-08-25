using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Warehouse.Wms.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class WorkflowRecoveryContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WorkflowKind",
                table: "WarehouseTasks",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkflowRecoveryStatus",
                table: "WarehouseTasks",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "Pending");

            migrationBuilder.AddColumn<string>(
                name: "WorkflowReference",
                table: "WarehouseTasks",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkflowSnapshotJson",
                table: "WarehouseTasks",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WorkflowKind",
                table: "WarehouseTasks");

            migrationBuilder.DropColumn(
                name: "WorkflowRecoveryStatus",
                table: "WarehouseTasks");

            migrationBuilder.DropColumn(
                name: "WorkflowReference",
                table: "WarehouseTasks");

            migrationBuilder.DropColumn(
                name: "WorkflowSnapshotJson",
                table: "WarehouseTasks");
        }
    }
}
