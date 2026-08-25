using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Warehouse.Wms.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BusinessWorkflowPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BusinessWorkflowIdempotency",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Scope = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Key = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    RequestHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    AggregateType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    AggregateKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessWorkflowIdempotency", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BusinessWorkflows",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AggregateType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    AggregateKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    WarehouseTaskNumber = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessWorkflows", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BusinessWorkflowStateHistories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AggregateType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    AggregateKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    FromStatus = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ToStatus = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    OperatorId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessWorkflowStateHistories", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BusinessWorkflowIdempotency_Scope_Key",
                table: "BusinessWorkflowIdempotency",
                columns: new[] { "Scope", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BusinessWorkflows_AggregateType_AggregateKey",
                table: "BusinessWorkflows",
                columns: new[] { "AggregateType", "AggregateKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BusinessWorkflows_UpdatedAt",
                table: "BusinessWorkflows",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_BusinessWorkflowStateHistories_AggregateType_AggregateKey_Version",
                table: "BusinessWorkflowStateHistories",
                columns: new[] { "AggregateType", "AggregateKey", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BusinessWorkflowIdempotency");

            migrationBuilder.DropTable(
                name: "BusinessWorkflows");

            migrationBuilder.DropTable(
                name: "BusinessWorkflowStateHistories");
        }
    }
}
