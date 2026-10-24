using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TenantFlow.Inventory.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// SPEC-0004 slice 1 (ADR-0020): reserved units on each balance, kept between zero and on-hand by a CHECK, and one
    /// reservation row per (tenant, order, product), never deleted by the application role, with the same forced
    /// row-level security as the other inventory tables (ADR-0011). The schema's default privileges already grant the
    /// application role its table access.
    /// </summary>
    public partial class AddStockReservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "reserved",
                schema: "inventory",
                table: "stock_balances",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "reservations",
                schema: "inventory",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    released_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reservations", x => new { x.tenant_id, x.order_id, x.product_id });
                    table.CheckConstraint("ck_reservations_quantity", "quantity > 0");
                    table.CheckConstraint("ck_reservations_status", "status IN ('reserved', 'released')");
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_stock_balances_reserved",
                schema: "inventory",
                table: "stock_balances",
                sql: "reserved >= 0 AND reserved <= on_hand");

            // A release marks rows released; the history of what an order held is never deleted by the application.
            migrationBuilder.Sql("REVOKE DELETE, TRUNCATE ON inventory.reservations FROM tenantflow_app;");

            migrationBuilder.Sql("ALTER TABLE inventory.reservations ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE inventory.reservations FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("""
                CREATE POLICY tenant_isolation ON inventory.reservations
                    USING (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "reservations",
                schema: "inventory");

            migrationBuilder.DropCheckConstraint(
                name: "ck_stock_balances_reserved",
                schema: "inventory",
                table: "stock_balances");

            migrationBuilder.DropColumn(
                name: "reserved",
                schema: "inventory",
                table: "stock_balances");
        }
    }
}
