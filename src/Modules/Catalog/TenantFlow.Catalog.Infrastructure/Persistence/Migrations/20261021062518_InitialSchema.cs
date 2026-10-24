using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TenantFlow.Catalog.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Creates the module schema, the application role's access to it, and the first tenant-owned table with
    /// row-level security (ADR-0011, ADR-0012). The Migrator runs this as the owner role.
    /// </summary>
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "catalog");
            migrationBuilder.Sql("GRANT USAGE ON SCHEMA catalog TO tenantflow_app;");
            migrationBuilder.Sql("ALTER DEFAULT PRIVILEGES IN SCHEMA catalog GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO tenantflow_app;");
            migrationBuilder.Sql("ALTER DEFAULT PRIVILEGES IN SCHEMA catalog GRANT USAGE, SELECT ON SEQUENCES TO tenantflow_app;");

            migrationBuilder.CreateTable(
                name: "products",
                schema: "catalog",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sku = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    price_amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    price_currency = table.Column<string>(type: "character(3)", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_products", x => x.id);
                    table.CheckConstraint("ck_products_price_amount", "price_amount >= 0");
                    table.CheckConstraint("ck_products_status", "status IN ('active', 'archived')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_products_tenant_id_sku_id",
                schema: "catalog",
                table: "products",
                columns: new[] { "tenant_id", "sku", "id" });

            // Tenant-aware uniqueness ignoring case (SPEC-0003 criterion 6). EF Core cannot model an expression index.
            migrationBuilder.Sql("CREATE UNIQUE INDEX ux_products_tenant_id_sku_lower ON catalog.products (tenant_id, lower(sku));");

            // Row-level security (ADR-0011): fail closed. Without app.tenant_id the comparison is null, so no row is
            // visible or writable; after a transaction that set it, a pooled connection reads '' (not null), which
            // nullif turns back into null instead of an invalid uuid. FORCE applies the policy to the owner too.
            migrationBuilder.Sql("ALTER TABLE catalog.products ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE catalog.products FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("""
                CREATE POLICY tenant_isolation ON catalog.products
                    USING (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "products",
                schema: "catalog");
        }
    }
}
