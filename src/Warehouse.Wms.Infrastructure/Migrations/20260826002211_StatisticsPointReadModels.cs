using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Warehouse.Wms.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class StatisticsPointReadModels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PointSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WarehouseCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ZoneCode = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Aisle = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Rack = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Level = table.Column<int>(type: "int", nullable: false),
                    LocationCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    PalletCode = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MaterialCode = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MaterialName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    BatchNumber = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Quantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    WeightKg = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    ObservedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SourceVersion = table.Column<long>(type: "bigint", nullable: false),
                    IsLocked = table.Column<bool>(type: "bit", nullable: false),
                    LockReason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TaskState = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LoadPoint = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PointSnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StatisticsBatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BatchId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Period = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    PeriodStart = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PeriodEnd = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    GeneratedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SourceVersion = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    WarehouseCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Freshness = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    InventoryQuantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    InventoryWeightKg = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    LocationUtilizationPercent = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    InboundQuantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    OutboundQuantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    TransferQuantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    TaskSuccessRatePercent = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    ExceptionCount = table.Column<int>(type: "int", nullable: false),
                    StocktakingDifferenceCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StatisticsBatches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StatisticsTaskStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BatchEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    State = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Count = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StatisticsTaskStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StatisticsTaskStates_StatisticsBatches_BatchEntityId",
                        column: x => x.BatchEntityId,
                        principalTable: "StatisticsBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StatisticsTrends",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BatchEntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PeriodStart = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    InboundQuantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    OutboundQuantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    TransferQuantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    ExceptionCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StatisticsTrends", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StatisticsTrends_StatisticsBatches_BatchEntityId",
                        column: x => x.BatchEntityId,
                        principalTable: "StatisticsBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PointSnapshots_LocationCode",
                table: "PointSnapshots",
                column: "LocationCode");

            migrationBuilder.CreateIndex(
                name: "IX_PointSnapshots_LocationCode_SourceVersion",
                table: "PointSnapshots",
                columns: new[] { "LocationCode", "SourceVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StatisticsBatches_BatchId",
                table: "StatisticsBatches",
                column: "BatchId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StatisticsBatches_Period_PeriodStart_PeriodEnd_WarehouseCode",
                table: "StatisticsBatches",
                columns: new[] { "Period", "PeriodStart", "PeriodEnd", "WarehouseCode" },
                unique: true,
                filter: "[WarehouseCode] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_StatisticsTaskStates_BatchEntityId",
                table: "StatisticsTaskStates",
                column: "BatchEntityId");

            migrationBuilder.CreateIndex(
                name: "IX_StatisticsTrends_BatchEntityId",
                table: "StatisticsTrends",
                column: "BatchEntityId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PointSnapshots");

            migrationBuilder.DropTable(
                name: "StatisticsTaskStates");

            migrationBuilder.DropTable(
                name: "StatisticsTrends");

            migrationBuilder.DropTable(
                name: "StatisticsBatches");
        }
    }
}
