using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TenantFlow.Audit.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// SPEC-0009 slice 4 (criterion 10, ADR-0025): the only way audit records leave. <c>audit.purge_expired()</c> runs as
    /// the owner, because the application role may not delete records (criterion 3), and deletes at most 500 records of
    /// the tenant set for row-level security, older than its own fixed cutoff of 400 days. It takes no argument, so no
    /// caller can move the cutoff; with no tenant set it deletes nothing.
    /// </summary>
    public partial class AddAuditPurgeFunction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE FUNCTION audit.purge_expired() RETURNS bigint
                    LANGUAGE sql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
                    WITH batch AS (
                        SELECT ctid FROM audit.audit_records
                        WHERE tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid
                          AND occurred_at < now() - interval '400 days'
                        LIMIT 500),
                         deleted AS (DELETE FROM audit.audit_records WHERE ctid IN (SELECT ctid FROM batch) RETURNING 1)
                    SELECT count(*) FROM deleted;
                $$;
                """);
            migrationBuilder.Sql("REVOKE ALL ON FUNCTION audit.purge_expired() FROM PUBLIC;");
            migrationBuilder.Sql("GRANT EXECUTE ON FUNCTION audit.purge_expired() TO tenantflow_app;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION audit.purge_expired();");
        }
    }
}
