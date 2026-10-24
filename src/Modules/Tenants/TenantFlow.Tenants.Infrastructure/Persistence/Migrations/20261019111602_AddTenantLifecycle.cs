using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TenantFlow.Tenants.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "tenants");

            migrationBuilder.CreateTable(
                name: "tenant_audit",
                schema: "tenants",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    actor_issuer = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    actor_subject = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    old_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    new_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_audit", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tenants",
                schema: "tenants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenants", x => x.id);
                    table.CheckConstraint("ck_tenants_status", "status IN ('active', 'suspended')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_tenant_audit_tenant_id_occurred_at",
                schema: "tenants",
                table: "tenant_audit",
                columns: new[] { "tenant_id", "occurred_at" });

            // Audit is append-only for the application (blueprint section 13): the schema's default
            // privileges grant UPDATE and DELETE on new tables, so take them back for this one.
            migrationBuilder.Sql("REVOKE UPDATE, DELETE, TRUNCATE ON tenants.tenant_audit FROM tenantflow_app;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tenant_audit",
                schema: "tenants");

            migrationBuilder.DropTable(
                name: "tenants",
                schema: "tenants");
        }
    }
}
