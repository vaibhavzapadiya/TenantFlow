using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TenantFlow.Operations.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// SPEC-0009 slice 7 (ADR-0027): the operations schema, the application role's access to it, support grants (platform-owned,
    /// not under row-level security, like tenants.tenants) and the module's outbox for their audit events. The Migrator runs this
    /// as the owner role.
    /// </summary>
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "operations");
            migrationBuilder.Sql("GRANT USAGE ON SCHEMA operations TO tenantflow_app;");
            migrationBuilder.Sql("ALTER DEFAULT PRIVILEGES IN SCHEMA operations GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO tenantflow_app;");

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "operations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    traceparent = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_messages", x => x.id);
                    table.CheckConstraint("ck_outbox_messages_attempts", "attempts >= 0");
                    table.CheckConstraint("ck_outbox_messages_version", "version >= 1");
                });

            migrationBuilder.CreateTable(
                name: "support_grants",
                schema: "operations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operator_ref = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ticket = table.Column<string>(type: "character varying(21)", maxLength: 21, nullable: false),
                    starts_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ended_by = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_support_grants", x => x.id);
                    table.CheckConstraint("ck_support_grants_window", "expires_at > starts_at");
                });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_pending",
                schema: "operations",
                table: "outbox_messages",
                column: "occurred_at",
                filter: "published_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_support_grants_operator_ref_tenant_id_expires_at",
                schema: "operations",
                table: "support_grants",
                columns: new[] { "operator_ref", "tenant_id", "expires_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "operations");

            migrationBuilder.DropTable(
                name: "support_grants",
                schema: "operations");

            migrationBuilder.Sql("ALTER DEFAULT PRIVILEGES IN SCHEMA operations REVOKE SELECT, INSERT, UPDATE, DELETE ON TABLES FROM tenantflow_app;");
            migrationBuilder.Sql("REVOKE USAGE ON SCHEMA operations FROM tenantflow_app;");
        }
    }
}
