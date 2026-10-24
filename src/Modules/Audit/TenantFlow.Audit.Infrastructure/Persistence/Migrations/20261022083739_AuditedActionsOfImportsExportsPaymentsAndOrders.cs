using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TenantFlow.Audit.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// SPEC-0009 slice 3: the actions and target types of imports, exports, downloads, refunds and paid order
    /// cancellations join the closed lists the database accepts.
    /// </summary>
    public partial class AuditedActionsOfImportsExportsPaymentsAndOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_records_action",
                schema: "audit",
                table: "audit_records");

            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_records_target_type",
                schema: "audit",
                table: "audit_records");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_records_action",
                schema: "audit",
                table: "audit_records",
                sql: "action IN ('export.downloaded', 'export.requested', 'import.requested', 'membership.added', 'membership.removed', 'membership.role_changed', 'order.cancellation_requested', 'refund.requested', 'tenant.created', 'tenant.reactivated', 'tenant.suspended')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_records_target_type",
                schema: "audit",
                table: "audit_records",
                sql: "target_type IN ('export_job', 'import_job', 'membership', 'order', 'refund', 'tenant')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_records_action",
                schema: "audit",
                table: "audit_records");

            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_records_target_type",
                schema: "audit",
                table: "audit_records");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_records_action",
                schema: "audit",
                table: "audit_records",
                sql: "action IN ('membership.added', 'membership.removed', 'membership.role_changed', 'tenant.created', 'tenant.reactivated', 'tenant.suspended')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_records_target_type",
                schema: "audit",
                table: "audit_records",
                sql: "target_type IN ('membership', 'tenant')");
        }
    }
}
