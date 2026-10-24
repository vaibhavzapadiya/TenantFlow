using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TenantFlow.Payments.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// SPEC-0006 slice 2: received webhook events, keyed by provider and event id for deduplication, with forced row-level
    /// security like every tenant-owned table (ADR-0011, ADR-0022).
    /// </summary>
    public partial class AddWebhookEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "webhook_events",
                schema: "payments",
                columns: table => new
                {
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    event_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook_events", x => new { x.provider, x.event_id });
                    table.CheckConstraint("ck_webhook_events_outcome", "outcome IN ('applied', 'ignored')");
                });

            // Row-level security (ADR-0011, ADR-0019): the same fail-closed policy as the other tenant-owned tables.
            migrationBuilder.Sql("ALTER TABLE payments.webhook_events ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE payments.webhook_events FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("""
                CREATE POLICY tenant_isolation ON payments.webhook_events
                    USING (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "webhook_events",
                schema: "payments");
        }
    }
}
