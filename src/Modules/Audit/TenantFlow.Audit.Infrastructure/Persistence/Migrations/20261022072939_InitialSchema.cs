using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TenantFlow.Audit.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// SPEC-0009 slice 2 (ADR-0026): the audit schema, the application role's access to it, the append-only
    /// <c>audit_records</c> table and the consumer's processed messages, both with forced row-level security. The
    /// Migrator runs this as the owner role.
    /// </summary>
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "audit");
            migrationBuilder.Sql("GRANT USAGE ON SCHEMA audit TO tenantflow_app;");
            migrationBuilder.Sql("ALTER DEFAULT PRIVILEGES IN SCHEMA audit GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO tenantflow_app;");
            migrationBuilder.Sql("ALTER DEFAULT PRIVILEGES IN SCHEMA audit GRANT USAGE, SELECT ON SEQUENCES TO tenantflow_app;");

            migrationBuilder.CreateTable(
                name: "audit_records",
                schema: "audit",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    actor_ref = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    target_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: false),
                    trace_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_records", x => x.id);
                    table.CheckConstraint("ck_audit_records_action", "action IN ('membership.added', 'membership.removed', 'membership.role_changed', 'tenant.created', 'tenant.reactivated', 'tenant.suspended')");
                    table.CheckConstraint("ck_audit_records_actor_kind", "actor_kind IN ('member', 'platform_admin', 'service', 'support_operator')");
                    table.CheckConstraint("ck_audit_records_target_type", "target_type IN ('membership', 'tenant')");
                });

            migrationBuilder.CreateTable(
                name: "processed_messages",
                schema: "audit",
                columns: table => new
                {
                    consumer = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_processed_messages", x => new { x.consumer, x.message_id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_records_tenant_id_occurred_at_id",
                schema: "audit",
                table: "audit_records",
                columns: new[] { "tenant_id", "occurred_at", "id" },
                descending: new[] { false, true, true });

            // Append-only, by two controls (ADR-0026 decision 4). The schema's default privileges would grant the
            // application role UPDATE and DELETE, so they are taken back here: it may only add and read records. The
            // trigger refuses an update from every role, the owner included. Rows leave only through the retention
            // function of SPEC-0009 slice 4.
            migrationBuilder.Sql("REVOKE UPDATE, DELETE, TRUNCATE ON audit.audit_records FROM tenantflow_app;");
            migrationBuilder.Sql("""
                CREATE FUNCTION audit.refuse_audit_record_update() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'audit records are append-only' USING ERRCODE = 'insufficient_privilege';
                END
                $$;
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER audit_records_append_only BEFORE UPDATE ON audit.audit_records
                    FOR EACH ROW EXECUTE FUNCTION audit.refuse_audit_record_update();
                """);

            // Row-level security (ADR-0011, ADR-0019): the same fail-closed policy as the other tenant-owned tables.
            foreach (var table in new[] { "audit_records", "processed_messages" })
            {
                migrationBuilder.Sql($"ALTER TABLE audit.{table} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE audit.{table} FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"""
                    CREATE POLICY tenant_isolation ON audit.{table}
                        USING (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid)
                        WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid);
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_records",
                schema: "audit");

            migrationBuilder.DropTable(
                name: "processed_messages",
                schema: "audit");

            migrationBuilder.Sql("DROP FUNCTION audit.refuse_audit_record_update();");
            migrationBuilder.Sql("ALTER DEFAULT PRIVILEGES IN SCHEMA audit REVOKE USAGE, SELECT ON SEQUENCES FROM tenantflow_app;");
            migrationBuilder.Sql("ALTER DEFAULT PRIVILEGES IN SCHEMA audit REVOKE SELECT, INSERT, UPDATE, DELETE ON TABLES FROM tenantflow_app;");
            migrationBuilder.Sql("REVOKE USAGE ON SCHEMA audit FROM tenantflow_app;");
        }
    }
}
