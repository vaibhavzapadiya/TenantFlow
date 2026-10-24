using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TenantFlow.ImportsExports.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// SPEC-0008 slice 2: the imports_exports schema, the application role's access to it, and stored files with forced
    /// row-level security (ADR-0011, ADR-0012, ADR-0024). The Migrator runs this as the owner role.
    /// </summary>
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "imports_exports");
            migrationBuilder.Sql("GRANT USAGE ON SCHEMA imports_exports TO tenantflow_app;");
            migrationBuilder.Sql("ALTER DEFAULT PRIVILEGES IN SCHEMA imports_exports GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO tenantflow_app;");
            migrationBuilder.Sql("ALTER DEFAULT PRIVILEGES IN SCHEMA imports_exports GRANT USAGE, SELECT ON SEQUENCES TO tenantflow_app;");

            migrationBuilder.CreateTable(
                name: "stored_files",
                schema: "imports_exports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    size = table.Column<long>(type: "bigint", nullable: true),
                    sha256 = table.Column<string>(type: "character(64)", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stored_files", x => x.id);
                    table.CheckConstraint("ck_stored_files_kind", "kind IN ('import_upload', 'export')");
                    table.CheckConstraint("ck_stored_files_size", "size IS NULL OR size >= 0");
                });

            migrationBuilder.CreateIndex(
                name: "ux_stored_files_key",
                schema: "imports_exports",
                table: "stored_files",
                column: "key",
                unique: true);

            // Row-level security (ADR-0011, ADR-0019): the same fail-closed policy as the other tenant-owned tables.
            migrationBuilder.Sql("ALTER TABLE imports_exports.stored_files ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE imports_exports.stored_files FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("""
                CREATE POLICY tenant_isolation ON imports_exports.stored_files
                    USING (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid);
                """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "stored_files",
                schema: "imports_exports");

            migrationBuilder.Sql("ALTER DEFAULT PRIVILEGES IN SCHEMA imports_exports REVOKE USAGE, SELECT ON SEQUENCES FROM tenantflow_app;");
            migrationBuilder.Sql("ALTER DEFAULT PRIVILEGES IN SCHEMA imports_exports REVOKE SELECT, INSERT, UPDATE, DELETE ON TABLES FROM tenantflow_app;");
            migrationBuilder.Sql("REVOKE USAGE ON SCHEMA imports_exports FROM tenantflow_app;");
        }
    }
}
