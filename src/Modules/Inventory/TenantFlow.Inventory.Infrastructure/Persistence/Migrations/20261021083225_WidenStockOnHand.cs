using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TenantFlow.Inventory.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Widens stock_balances.on_hand from integer to bigint (code review of M3): enough maximum adjustments overflowed
    /// a 32-bit balance into a 500. A widening is safe for existing rows; the CHECK (on_hand >= 0) and the policies stay.
    /// </summary>
    public partial class WidenStockOnHand : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<long>(
                name: "on_hand",
                schema: "inventory",
                table: "stock_balances",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "on_hand",
                schema: "inventory",
                table: "stock_balances",
                type: "integer",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint");
        }
    }
}
