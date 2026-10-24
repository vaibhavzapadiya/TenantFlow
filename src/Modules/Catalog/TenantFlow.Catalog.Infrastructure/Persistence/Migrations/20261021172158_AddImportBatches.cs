using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TenantFlow.Catalog.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// SPEC-0008 criterion 5 (ADR-0024 decision 3): every applied import batch, keyed by job and batch number, with its
    /// result, written in the batch's transaction so a repeated batch replays instead of applying again. Tenant-owned,
    /// forced row-level security. Not in the EF model: only CatalogStore's SQL uses it. Additive.
    /// </summary>
    public partial class AddImportBatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE catalog.import_batches (
                    tenant_id uuid NOT NULL,
                    job_id uuid NOT NULL,
                    batch_number integer NOT NULL,
                    result jsonb NOT NULL,
                    applied_at timestamp with time zone NOT NULL,
                    CONSTRAINT pk_import_batches PRIMARY KEY (tenant_id, job_id, batch_number),
                    CONSTRAINT ck_import_batches_batch_number CHECK (batch_number >= 1));
                ALTER TABLE catalog.import_batches ENABLE ROW LEVEL SECURITY;
                ALTER TABLE catalog.import_batches FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON catalog.import_batches
                    USING (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE catalog.import_batches;");
        }
    }
}
