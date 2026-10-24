using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TenantFlow.Payments.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// SPEC-0007 slice 3 (ADR-0023 decision 4): refunds with their audit fields and forced row-level security, one
    /// cancellation refund per payment, the payment's refunded amount (never above the captured amount), and the refund an
    /// idempotency key started. Additive.
    /// </summary>
    public partial class AddRefunds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "refunded_amount",
                schema: "payments",
                table: "payments",
                type: "numeric(19,4)",
                precision: 19,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<Guid>(
                name: "refund_id",
                schema: "payments",
                table: "idempotency_keys",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "refunds",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    currency = table.Column<string>(type: "character(3)", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    requested_by_membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    provider_refund_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    failure_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refunds", x => x.id);
                    table.CheckConstraint("ck_refunds_amount", "amount > 0");
                    table.CheckConstraint("ck_refunds_kind", "kind IN ('refund', 'cancellation')");
                    table.CheckConstraint("ck_refunds_status", "status IN ('processing', 'succeeded', 'failed', 'unknown', 'requires_review')");
                    table.ForeignKey(
                        name: "fk_refunds_payments_payment_id",
                        column: x => x.payment_id,
                        principalSchema: "payments",
                        principalTable: "payments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_payments_refunded_amount",
                schema: "payments",
                table: "payments",
                sql: "refunded_amount >= 0 AND refunded_amount <= amount");

            migrationBuilder.CreateIndex(
                name: "ix_refunds_tenant_id_payment_id",
                schema: "payments",
                table: "refunds",
                columns: new[] { "tenant_id", "payment_id" });

            migrationBuilder.CreateIndex(
                name: "ux_refunds_one_cancellation_per_payment",
                schema: "payments",
                table: "refunds",
                column: "payment_id",
                unique: true,
                filter: "kind = 'cancellation'");

            // Row-level security (ADR-0011, ADR-0019): the same fail-closed policy as the other tenant-owned tables.
            migrationBuilder.Sql("ALTER TABLE payments.refunds ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE payments.refunds FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("""
                CREATE POLICY tenant_isolation ON payments.refunds
                    USING (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "refunds",
                schema: "payments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_payments_refunded_amount",
                schema: "payments",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "refunded_amount",
                schema: "payments",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "refund_id",
                schema: "payments",
                table: "idempotency_keys");
        }
    }
}
