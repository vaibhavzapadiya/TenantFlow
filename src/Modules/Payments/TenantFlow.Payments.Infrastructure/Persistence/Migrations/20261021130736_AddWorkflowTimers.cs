using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TenantFlow.Payments.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// SPEC-0006 slice 4: the module's workflow timers (ADR-0022 decision 7). Ids, tenant and due time only, read across
    /// tenants by the Worker's timer runner, so not under row-level security, like the outbox; the work each timer starts
    /// runs in its tenant under RLS.
    /// </summary>
    public partial class AddWorkflowTimers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "workflow_timers",
                schema: "payments",
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
                name: "ix_workflow_timers_open",
                schema: "payments",
                table: "workflow_timers",
                column: "due_at",
                filter: "completed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_workflow_timers_kind_subject_id",
                schema: "payments",
                table: "workflow_timers",
                columns: new[] { "kind", "subject_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "workflow_timers",
                schema: "payments");
        }
    }
}
