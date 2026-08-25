using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Warehouse.Wms.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TaskPersistenceV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ResourceLocks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ResourceType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ResourceId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    OwnerTaskNumber = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    LockToken = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AcquiredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResourceLocks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TaskIdempotencyKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Scope = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Key = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RequestHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    TaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskIdempotencyKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WarehouseTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TaskNumber = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    TaskType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    State = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WarehouseTasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TaskStateHistories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FromState = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ToState = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Operator = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    ErrorCode = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskStateHistories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskStateHistories_WarehouseTasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "WarehouseTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ResourceLocks_ExpiresAt_ReleasedAt",
                table: "ResourceLocks",
                columns: new[] { "ExpiresAt", "ReleasedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ResourceLocks_ResourceType_ResourceId",
                table: "ResourceLocks",
                columns: new[] { "ResourceType", "ResourceId" },
                unique: true,
                filter: "[ReleasedAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TaskIdempotencyKeys_Scope_Key",
                table: "TaskIdempotencyKeys",
                columns: new[] { "Scope", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskIdempotencyKeys_TaskId",
                table: "TaskIdempotencyKeys",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskStateHistories_TaskId_Version",
                table: "TaskStateHistories",
                columns: new[] { "TaskId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WarehouseTasks_TaskNumber",
                table: "WarehouseTasks",
                column: "TaskNumber",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ResourceLocks");

            migrationBuilder.DropTable(
                name: "TaskIdempotencyKeys");

            migrationBuilder.DropTable(
                name: "TaskStateHistories");

            migrationBuilder.DropTable(
                name: "WarehouseTasks");
        }
    }
}
