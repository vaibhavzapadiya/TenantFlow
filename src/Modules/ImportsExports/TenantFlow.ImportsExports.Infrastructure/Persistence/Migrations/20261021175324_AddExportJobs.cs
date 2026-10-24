using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TenantFlow.ImportsExports.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExportJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "export_jobs",
                schema: "imports_exports",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    request_hash = table.Column<string>(type: "character(64)", nullable: false),
                    row_count = table.Column<int>(type: "integer", nullable: false),
                    failure_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_export_jobs", x => x.id);
                    table.CheckConstraint("ck_export_jobs_row_count", "row_count >= 0");
                    table.CheckConstraint("ck_export_jobs_status", "status IN ('receiving', 'queued', 'running', 'succeeded', 'failed')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_export_jobs_tenant_id_status",
                schema: "imports_exports",
                table: "export_jobs",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_export_jobs_tenant_id_idempotency_key",
                schema: "imports_exports",
                table: "export_jobs",
                columns: new[] { "tenant_id", "idempotency_key" },
                unique: true);

            // Row-level security (ADR-0011, ADR-0019): the same fail-closed policy as the other tenant-owned tables.
            migrationBuilder.Sql("ALTER TABLE imports_exports.export_jobs ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE imports_exports.export_jobs FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("""
                CREATE POLICY tenant_isolation ON imports_exports.export_jobs
                    USING (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "export_jobs",
                schema: "imports_exports");
        }
    }
}
