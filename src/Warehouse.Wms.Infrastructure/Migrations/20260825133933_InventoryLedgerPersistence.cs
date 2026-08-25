using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Warehouse.Wms.Infrastructure.Migrations;

public partial class InventoryLedgerPersistence : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "InventoryBalances",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                BalanceKey = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                MaterialId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                PalletId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                LocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                BatchNumber = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                Quantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                WeightKg = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                Version = table.Column<int>(type: "int", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_InventoryBalances", x => x.Id);
                table.CheckConstraint("CK_InventoryBalances_NonNegative", "Quantity >= 0 AND WeightKg >= 0");
            });

        migrationBuilder.CreateTable(
            name: "InventoryTransactions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                IdempotencyKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                Type = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                MaterialId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                PalletId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                LocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                SourceLocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                DestinationLocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                BatchNumber = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                Quantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                WeightKg = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                StatusBefore = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                StatusAfter = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                Fingerprint = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                SourceDocumentId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                TaskNumber = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                OperatorId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_InventoryTransactions", x => x.Id);
                table.CheckConstraint(
                    "CK_InventoryTransactions_QuantityWeight",
                    "Type = 'Adjustment' OR (Quantity >= 0 AND WeightKg >= 0)");
            });

        migrationBuilder.CreateIndex(
            name: "IX_InventoryBalances_BalanceKey",
            table: "InventoryBalances",
            column: "BalanceKey",
            unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_InventoryBalances_MaterialId_LocationId",
            table: "InventoryBalances",
            columns: new[] { "MaterialId", "LocationId" });
        migrationBuilder.CreateIndex(
            name: "IX_InventoryTransactions_IdempotencyKey",
            table: "InventoryTransactions",
            column: "IdempotencyKey",
            unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_InventoryTransactions_MaterialId_OccurredAt",
            table: "InventoryTransactions",
            columns: new[] { "MaterialId", "OccurredAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "InventoryBalances");
        migrationBuilder.DropTable(name: "InventoryTransactions");
    }
}
