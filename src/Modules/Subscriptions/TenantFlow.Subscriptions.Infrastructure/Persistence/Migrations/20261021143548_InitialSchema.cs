using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TenantFlow.Subscriptions.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// SPEC-0007 slice 1: the subscriptions schema, the application role's access to it, plans and subscriptions with forced
    /// row-level security, and the module's workflow timers, which are not under RLS (ADR-0011, ADR-0012, ADR-0023). The
    /// Migrator runs this as the owner role.
    /// </summary>
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "subscriptions");
            migrationBuilder.Sql("GRANT USAGE ON SCHEMA subscriptions TO tenantflow_app;");
            migrationBuilder.Sql("ALTER DEFAULT PRIVILEGES IN SCHEMA subscriptions GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO tenantflow_app;");
            migrationBuilder.Sql("ALTER DEFAULT PRIVILEGES IN SCHEMA subscriptions GRANT USAGE, SELECT ON SEQUENCES TO tenantflow_app;");

            migrationBuilder.CreateTable(
                name: "plans",
                schema: "subscriptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    currency = table.Column<string>(type: "character(3)", nullable: false),
                    interval = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    trial_days = table.Column<int>(type: "integer", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_plans", x => x.id);
                    table.CheckConstraint("ck_plans_amount", "amount > 0");
                    table.CheckConstraint("ck_plans_interval", "interval IN ('month', 'year')");
                    table.CheckConstraint("ck_plans_trial_days", "trial_days BETWEEN 0 AND 90");
                });

            migrationBuilder.CreateTable(
                name: "subscriptions",
                schema: "subscriptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscriber_membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    payment_method = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    currency = table.Column<string>(type: "character(3)", nullable: false),
                    interval = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    anchor_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    period = table.Column<int>(type: "integer", nullable: false),
                    period_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    failed_attempts = table.Column<int>(type: "integer", nullable: false),
                    cancel_at_period_end = table.Column<bool>(type: "boolean", nullable: false),
                    cancel_reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscriptions", x => x.id);
                    table.CheckConstraint("ck_subscriptions_amount", "amount > 0");
                    table.CheckConstraint("ck_subscriptions_cancel_reason", "cancel_reason IS NULL OR cancel_reason IN ('requested', 'payment_failed')");
                    table.CheckConstraint("ck_subscriptions_period", "period >= 0 AND failed_attempts >= 0");
                    table.CheckConstraint("ck_subscriptions_status", "status IN ('trialing', 'incomplete', 'active', 'past_due', 'cancelled')");
                });

            migrationBuilder.CreateTable(
                name: "workflow_timers",
                schema: "subscriptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workflow_timers", x => x.id);
                    table.CheckConstraint("ck_workflow_timers_attempts", "attempts >= 0");
                });

            migrationBuilder.CreateIndex(
                name: "ix_plans_tenant_id_created_at",
                schema: "subscriptions",
                table: "plans",
                columns: new[] { "tenant_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_subscriptions_live_subscriber_plan",
                schema: "subscriptions",
                table: "subscriptions",
                columns: new[] { "tenant_id", "subscriber_membership_id", "plan_id" },
                unique: true,
                filter: "status <> 'cancelled'");

            migrationBuilder.CreateIndex(
                name: "ix_workflow_timers_open",
                schema: "subscriptions",
                table: "workflow_timers",
                column: "due_at",
                filter: "completed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_workflow_timers_kind_subject_id",
                schema: "subscriptions",
                table: "workflow_timers",
                columns: new[] { "kind", "subject_id" },
                unique: true);

            // Row-level security (ADR-0011, ADR-0019): the same fail-closed policy as the other tenant-owned tables.
            foreach (var table in new[] { "plans", "subscriptions" })
            {
                migrationBuilder.Sql($"ALTER TABLE subscriptions.{table} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE subscriptions.{table} FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"""
                    CREATE POLICY tenant_isolation ON subscriptions.{table}
                        USING (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid)
                        WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid);
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "plans",
                schema: "subscriptions");

            migrationBuilder.DropTable(
                name: "subscriptions",
                schema: "subscriptions");

            migrationBuilder.DropTable(
                name: "workflow_timers",
                schema: "subscriptions");

            migrationBuilder.Sql("ALTER DEFAULT PRIVILEGES IN SCHEMA subscriptions REVOKE USAGE, SELECT ON SEQUENCES FROM tenantflow_app;");
            migrationBuilder.Sql("ALTER DEFAULT PRIVILEGES IN SCHEMA subscriptions REVOKE SELECT, INSERT, UPDATE, DELETE ON TABLES FROM tenantflow_app;");
            migrationBuilder.Sql("REVOKE USAGE ON SCHEMA subscriptions FROM tenantflow_app;");
        }
    }
}
