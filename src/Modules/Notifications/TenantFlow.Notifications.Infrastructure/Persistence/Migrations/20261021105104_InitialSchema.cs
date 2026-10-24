using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TenantFlow.Notifications.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// SPEC-0005 slice 3: the notifications schema, the application role's access to it, and the notification and
    /// processed-message tables with forced row-level security (ADR-0011, ADR-0012, ADR-0021). The Migrator runs this as
    /// the owner role.
    /// </summary>
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "notifications");
            migrationBuilder.Sql("GRANT USAGE ON SCHEMA notifications TO tenantflow_app;");
            migrationBuilder.Sql("ALTER DEFAULT PRIVILEGES IN SCHEMA notifications GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO tenantflow_app;");
            migrationBuilder.Sql("ALTER DEFAULT PRIVILEGES IN SCHEMA notifications GRANT USAGE, SELECT ON SEQUENCES TO tenantflow_app;");

            migrationBuilder.CreateTable(
                name: "notifications",
                schema: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipient_membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notifications", x => x.id);
                    table.CheckConstraint("ck_notifications_kind", "kind IN ('order_reserved', 'order_cancelled')");
                });

            migrationBuilder.CreateTable(
                name: "processed_messages",
                schema: "notifications",
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
                name: "ix_notifications_tenant_id_recipient_membership_id",
                schema: "notifications",
                table: "notifications",
                columns: new[] { "tenant_id", "recipient_membership_id" });

            // Row-level security (ADR-0011, ADR-0019): the same fail-closed policy as the other tenant-owned tables.
            foreach (var table in new[] { "notifications", "processed_messages" })
            {
                migrationBuilder.Sql($"ALTER TABLE notifications.{table} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE notifications.{table} FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"""
                    CREATE POLICY tenant_isolation ON notifications.{table}
                        USING (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid)
                        WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid);
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notifications",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "processed_messages",
                schema: "notifications");

            migrationBuilder.Sql("ALTER DEFAULT PRIVILEGES IN SCHEMA notifications REVOKE USAGE, SELECT ON SEQUENCES FROM tenantflow_app;");
            migrationBuilder.Sql("ALTER DEFAULT PRIVILEGES IN SCHEMA notifications REVOKE SELECT, INSERT, UPDATE, DELETE ON TABLES FROM tenantflow_app;");
            migrationBuilder.Sql("REVOKE USAGE ON SCHEMA notifications FROM tenantflow_app;");
        }
    }
}
