using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TenantFlow.IdentityAccess.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMemberships : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "identity_access");

            migrationBuilder.CreateTable(
                name: "membership_audit",
                schema: "identity_access",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    actor_issuer = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    actor_subject = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    old_role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    new_role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_membership_audit", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "memberships",
                schema: "identity_access",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    issuer = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    subject = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_memberships", x => x.id);
                    table.CheckConstraint("ck_memberships_role", "role IN ('tenant-admin', 'customer')");
                    table.CheckConstraint("ck_memberships_status", "status IN ('active', 'removed')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_membership_audit_tenant_id_occurred_at",
                schema: "identity_access",
                table: "membership_audit",
                columns: new[] { "tenant_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_memberships_issuer_subject",
                schema: "identity_access",
                table: "memberships",
                columns: new[] { "issuer", "subject" });

            migrationBuilder.CreateIndex(
                name: "ux_memberships_tenant_id_id",
                schema: "identity_access",
                table: "memberships",
                columns: new[] { "tenant_id", "id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_memberships_tenant_id_issuer_subject_active",
                schema: "identity_access",
                table: "memberships",
                columns: new[] { "tenant_id", "issuer", "subject" },
                unique: true,
                filter: "status = 'active'");

            // Audit is append-only for the application (blueprint section 13): the schema's default
            // privileges grant UPDATE and DELETE on new tables, so take them back for this one.
            migrationBuilder.Sql("REVOKE UPDATE, DELETE, TRUNCATE ON identity_access.membership_audit FROM tenantflow_app;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "membership_audit",
                schema: "identity_access");

            migrationBuilder.DropTable(
                name: "memberships",
                schema: "identity_access");
        }
    }
}
