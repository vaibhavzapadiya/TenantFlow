using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TenantFlow.Tenants.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUniqueTenantName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Tenant names are unique ignoring case (SPEC-0002 criterion 26). Names are stored trimmed, so this also
            // ignores surrounding whitespace. EF Core cannot model an expression index, so it is created in SQL; the
            // store maps a violation of exactly this index to tenant.name_taken. Fails, naming the index, if the table
            // already holds two names that differ only by case: resolve them by hand first (expand-contract).
            migrationBuilder.Sql("CREATE UNIQUE INDEX ux_tenants_name_lower ON tenants.tenants (lower(name));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX tenants.ux_tenants_name_lower;");
        }
    }
}
