using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TenantFlow.ImportsExports.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// SPEC-0008 slice 3 (ADR-0024 decision 3): import jobs and their row errors with forced row-level security, and the
    /// module's due rows (ids only, not under RLS, as in the other modules). Additive.
    /// </summary>
    public partial class AddImportJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "import_jobs",
                schema: "imports_exports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    request_hash = table.Column<string>(type: "character(64)", nullable: false),
                    checkpoint_row = table.Column<int>(type: "integer", nullable: false),
                    created_count = table.Column<int>(type: "integer", nullable: false),
                    updated_count = table.Column<int>(type: "integer", nullable: false),
                    failed_count = table.Column<int>(type: "integer", nullable: false),
                    failure_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_jobs", x => x.id);
                    table.CheckConstraint("ck_import_jobs_counts", "checkpoint_row >= 0 AND created_count >= 0 AND updated_count >= 0 AND failed_count >= 0");
                    table.CheckConstraint("ck_import_jobs_status", "status IN ('receiving', 'queued', 'running', 'succeeded', 'failed')");
                });

            migrationBuilder.CreateTable(
                name: "import_row_errors",
                schema: "imports_exports",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    row = table.Column<int>(type: "integer", nullable: false),
                    column = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_import_row_errors", x => new { x.tenant_id, x.job_id, x.row, x.column });
                });

            migrationBuilder.CreateTable(
                name: "workflow_timers",
                schema: "imports_exports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failures = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    dead_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workflow_timers", x => x.id);
                    table.CheckConstraint("ck_workflow_timers_attempts", "attempts >= 0");
                    table.CheckConstraint("ck_workflow_timers_failures", "failures >= 0");
                });

            migrationBuilder.CreateIndex(
                name: "ix_import_jobs_tenant_id_status",
                schema: "imports_exports",
                table: "import_jobs",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_import_jobs_tenant_id_idempotency_key",
                schema: "imports_exports",
                table: "import_jobs",
                columns: new[] { "tenant_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_workflow_timers_open",
                schema: "imports_exports",
                table: "workflow_timers",
                column: "due_at",
                filter: "completed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_workflow_timers_kind_subject_id",
                schema: "imports_exports",
                table: "workflow_timers",
                columns: new[] { "kind", "subject_id" },
                unique: true);

            // Row-level security (ADR-0011, ADR-0019): the same fail-closed policy as the other tenant-owned tables.
            foreach (var table in new[] { "import_jobs", "import_row_errors" })
            {
                migrationBuilder.Sql($"ALTER TABLE imports_exports.{table} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE imports_exports.{table} FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"""
                    CREATE POLICY tenant_isolation ON imports_exports.{table}
                        USING (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid)
                        WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid);
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "import_jobs",
                schema: "imports_exports");

            migrationBuilder.DropTable(
                name: "import_row_errors",
                schema: "imports_exports");

            migrationBuilder.DropTable(
                name: "workflow_timers",
                schema: "imports_exports");
        }
    }
}
