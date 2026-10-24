using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TenantFlow.Tenants.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Creates the module schema and the application role's access to it (ADR-0011, ADR-0012). The
    /// Migrator runs this as the owner role, so default privileges apply to tables the owner creates later.
    /// The application role may read and write rows, never change the schema.
    /// </summary>
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(name: "tenants");
            migrationBuilder.Sql("GRANT USAGE ON SCHEMA tenants TO tenantflow_app;");
            migrationBuilder.Sql("ALTER DEFAULT PRIVILEGES IN SCHEMA tenants GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO tenantflow_app;");
            migrationBuilder.Sql("ALTER DEFAULT PRIVILEGES IN SCHEMA tenants GRANT USAGE, SELECT ON SEQUENCES TO tenantflow_app;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER DEFAULT PRIVILEGES IN SCHEMA tenants REVOKE USAGE, SELECT ON SEQUENCES FROM tenantflow_app;");
            migrationBuilder.Sql("ALTER DEFAULT PRIVILEGES IN SCHEMA tenants REVOKE SELECT, INSERT, UPDATE, DELETE ON TABLES FROM tenantflow_app;");
            migrationBuilder.Sql("REVOKE USAGE ON SCHEMA tenants FROM tenantflow_app;");
        }
    }
}
