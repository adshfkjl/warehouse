using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Warehouse.Wms.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TaskDispatchContextModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DispatchContextJson",
                table: "WarehouseTasks",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DispatchContextJson",
                table: "WarehouseTasks");
        }
    }
}
