using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TenantFlow.Orders.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// SPEC-0004 slice 2: the orders schema, the application role's access to it, and the order and order-line tables
    /// with forced row-level security (ADR-0011, ADR-0012). The Migrator runs this as the owner role.
    /// </summary>
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "orders");
            migrationBuilder.Sql("GRANT USAGE ON SCHEMA orders TO tenantflow_app;");
            migrationBuilder.Sql("ALTER DEFAULT PRIVILEGES IN SCHEMA orders GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO tenantflow_app;");
            migrationBuilder.Sql("ALTER DEFAULT PRIVILEGES IN SCHEMA orders GRANT USAGE, SELECT ON SEQUENCES TO tenantflow_app;");

            migrationBuilder.CreateTable(
                name: "orders",
                schema: "orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    currency = table.Column<string>(type: "character(3)", nullable: false),
                    total_amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    rejection_reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_orders", x => x.id);
                    table.CheckConstraint("ck_orders_status", "status IN ('draft', 'pending_reservation', 'pending_payment', 'rejected', 'cancelled')");
                    table.CheckConstraint("ck_orders_total_amount", "total_amount >= 0");
                });

            migrationBuilder.CreateTable(
                name: "order_lines",
                schema: "orders",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_no = table.Column<int>(type: "integer", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sku = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    line_total = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_lines", x => x.id);
                    table.CheckConstraint("ck_order_lines_quantity", "quantity BETWEEN 1 AND 10000");
                    table.CheckConstraint("ck_order_lines_unit_price", "unit_price >= 0");
                    table.ForeignKey(
                        name: "fk_order_lines_orders",
                        column: x => x.order_id,
                        principalSchema: "orders",
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_order_lines_order_id",
                schema: "orders",
                table: "order_lines",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "ix_orders_tenant_id_customer_membership_id",
                schema: "orders",
                table: "orders",
                columns: new[] { "tenant_id", "customer_membership_id" });

            // Row-level security (ADR-0011, ADR-0019): the same fail-closed policy as the other tenant-owned tables.
            foreach (var table in new[] { "orders", "order_lines" })
            {
                migrationBuilder.Sql($"ALTER TABLE orders.{table} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE orders.{table} FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"""
                    CREATE POLICY tenant_isolation ON orders.{table}
                        USING (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid)
                        WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid);
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "order_lines",
                schema: "orders");

            migrationBuilder.DropTable(
                name: "orders",
                schema: "orders");

            migrationBuilder.Sql("ALTER DEFAULT PRIVILEGES IN SCHEMA orders REVOKE USAGE, SELECT ON SEQUENCES FROM tenantflow_app;");
            migrationBuilder.Sql("ALTER DEFAULT PRIVILEGES IN SCHEMA orders REVOKE SELECT, INSERT, UPDATE, DELETE ON TABLES FROM tenantflow_app;");
            migrationBuilder.Sql("REVOKE USAGE ON SCHEMA orders FROM tenantflow_app;");
        }
    }
}
