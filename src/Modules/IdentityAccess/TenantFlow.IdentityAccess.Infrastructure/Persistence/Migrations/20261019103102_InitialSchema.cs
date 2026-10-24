using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TenantFlow.IdentityAccess.Infrastructure.Persistence.Migrations
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
            migrationBuilder.EnsureSchema(name: "identity_access");
            migrationBuilder.Sql("GRANT USAGE ON SCHEMA identity_access TO tenantflow_app;");
            migrationBuilder.Sql("ALTER DEFAULT PRIVILEGES IN SCHEMA identity_access GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO tenantflow_app;");
            migrationBuilder.Sql("ALTER DEFAULT PRIVILEGES IN SCHEMA identity_access GRANT USAGE, SELECT ON SEQUENCES TO tenantflow_app;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER DEFAULT PRIVILEGES IN SCHEMA identity_access REVOKE USAGE, SELECT ON SEQUENCES FROM tenantflow_app;");
            migrationBuilder.Sql("ALTER DEFAULT PRIVILEGES IN SCHEMA identity_access REVOKE SELECT, INSERT, UPDATE, DELETE ON TABLES FROM tenantflow_app;");
            migrationBuilder.Sql("REVOKE USAGE ON SCHEMA identity_access FROM tenantflow_app;");
        }
    }
}
