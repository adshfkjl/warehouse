using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PLCManagement.API.Migrations
{
    /// <inheritdoc />
    public partial class AddInventoryCheckTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InventoryCheckTasks",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TaskNo = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    PLCID = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    TrayStart = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    TrayEnd = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    TotalCount = table.Column<int>(type: "int", nullable: false),
                    SuccessCount = table.Column<int>(type: "int", nullable: false),
                    FailedCount = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Message = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryCheckTasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InventoryCheckItems",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TaskId = table.Column<long>(type: "bigint", nullable: false),
                    PLCID = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Tray = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Shelf = table.Column<int>(type: "int", nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false),
                    OriginalShelfStatus = table.Column<int>(type: "int", nullable: false),
                    OutboundLoadingPoint = table.Column<int>(type: "int", nullable: true),
                    OutboundStatus = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    OutboundOperationLogId = table.Column<long>(type: "bigint", nullable: true),
                    OutboundStartedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    OutboundCompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    OutboundMessage = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    InboundStatus = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    InboundLoadingPoint = table.Column<int>(type: "int", nullable: true),
                    InboundScheduledAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    InboundMessage = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryCheckItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InventoryCheckItems_InventoryCheckTasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "InventoryCheckTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryCheckItems_TaskId_Tray",
                table: "InventoryCheckItems",
                columns: new[] { "TaskId", "Tray" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryCheckTasks_TaskNo",
                table: "InventoryCheckTasks",
                column: "TaskNo",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InventoryCheckItems");

            migrationBuilder.DropTable(
                name: "InventoryCheckTasks");
        }
    }
}
