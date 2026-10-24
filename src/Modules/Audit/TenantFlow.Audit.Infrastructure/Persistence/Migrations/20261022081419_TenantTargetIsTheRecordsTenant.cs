using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TenantFlow.Audit.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// SPEC-0009 slice 2 review fix: a record whose target is a tenant names its own tenant, so one tenant's log can never
    /// show another tenant's id. A separate migration, so a database that already ran the initial schema gets it too.
    /// </summary>
    public partial class TenantTargetIsTheRecordsTenant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_records_tenant_target",
                schema: "audit",
                table: "audit_records",
                sql: "target_type <> 'tenant' OR target_id = tenant_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_records_tenant_target",
                schema: "audit",
                table: "audit_records");
        }
    }
}
