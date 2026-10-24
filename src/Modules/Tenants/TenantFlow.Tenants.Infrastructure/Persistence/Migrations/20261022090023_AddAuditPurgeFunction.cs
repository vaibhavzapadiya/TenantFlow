using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TenantFlow.Tenants.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// SPEC-0009 slice 4 (criterion 10, decision 15): the M2 table <c>tenants.tenant_audit</c> loses rows older than the
    /// audit retention (400 days) through <c>tenants.purge_expired_audit()</c>. It runs as the owner, because the
    /// application role may only add and read audit rows, and deletes at most 500 rows of the tenant set in
    /// <c>app.tenant_id</c>, older than its own fixed cutoff; with no tenant set it deletes nothing.
    /// </summary>
    public partial class AddAuditPurgeFunction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE FUNCTION tenants.purge_expired_audit() RETURNS bigint
                    LANGUAGE sql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
                    WITH batch AS (
                        SELECT ctid FROM tenants.tenant_audit
                        WHERE tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid
                          AND occurred_at < now() - interval '400 days'
                        LIMIT 500),
                         deleted AS (DELETE FROM tenants.tenant_audit WHERE ctid IN (SELECT ctid FROM batch) RETURNING 1)
                    SELECT count(*) FROM deleted;
                $$;
                """);
            migrationBuilder.Sql("REVOKE ALL ON FUNCTION tenants.purge_expired_audit() FROM PUBLIC;");
            migrationBuilder.Sql("GRANT EXECUTE ON FUNCTION tenants.purge_expired_audit() TO tenantflow_app;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION tenants.purge_expired_audit();");
        }
    }
}
