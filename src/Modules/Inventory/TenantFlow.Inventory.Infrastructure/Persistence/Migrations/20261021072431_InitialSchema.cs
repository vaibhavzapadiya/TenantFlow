using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TenantFlow.Inventory.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Creates the inventory schema, the application role's access to it, the balance and adjustment tables with
    /// row-level security, and makes adjustments append-only (ADR-0011, ADR-0012). The Migrator runs this as the owner.
    /// </summary>
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "inventory");
            migrationBuilder.Sql("GRANT USAGE ON SCHEMA inventory TO tenantflow_app;");
            migrationBuilder.Sql("ALTER DEFAULT PRIVILEGES IN SCHEMA inventory GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO tenantflow_app;");
            migrationBuilder.Sql("ALTER DEFAULT PRIVILEGES IN SCHEMA inventory GRANT USAGE, SELECT ON SEQUENCES TO tenantflow_app;");

            migrationBuilder.CreateTable(
                name: "stock_adjustments",
                schema: "inventory",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    delta = table.Column<int>(type: "integer", nullable: false),
                    reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    actor_membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_adjustments", x => x.id);
                    table.CheckConstraint("ck_stock_adjustments_delta", "delta <> 0");
                });

            migrationBuilder.CreateTable(
                name: "stock_balances",
                schema: "inventory",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    on_hand = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_balances", x => new { x.tenant_id, x.product_id });
                    table.CheckConstraint("ck_stock_balances_on_hand", "on_hand >= 0");
                });

            migrationBuilder.CreateIndex(
                name: "ix_stock_adjustments_tenant_id_product_id_occurred_at",
                schema: "inventory",
                table: "stock_adjustments",
                columns: new[] { "tenant_id", "product_id", "occurred_at" });

            // Adjustments are append-only for the application role, like the membership audit.
            migrationBuilder.Sql("REVOKE UPDATE, DELETE, TRUNCATE ON inventory.stock_adjustments FROM tenantflow_app;");

            // Row-level security (ADR-0011, ADR-0019): the same fail-closed policy as catalog.products.
            foreach (var table in new[] { "stock_balances", "stock_adjustments" })
            {
                migrationBuilder.Sql($"ALTER TABLE inventory.{table} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE inventory.{table} FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"""
                    CREATE POLICY tenant_isolation ON inventory.{table}
                        USING (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid)
                        WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid);
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "stock_adjustments",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "stock_balances",
                schema: "inventory");
        }
    }
}
