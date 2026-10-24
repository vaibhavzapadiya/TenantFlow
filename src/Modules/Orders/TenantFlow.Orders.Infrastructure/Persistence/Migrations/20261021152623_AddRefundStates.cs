using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TenantFlow.Orders.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// SPEC-0007 slice 4 (ADR-0023 decision 5): the refund states partially_refunded, cancellation_pending and refunded in the
    /// status check, and who cancelled a paid order and why (audit). Additive: the check only widens.
    /// </summary>
    public partial class AddRefundStates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_orders_status",
                schema: "orders",
                table: "orders");

            migrationBuilder.AddColumn<string>(
                name: "cancellation_reason",
                schema: "orders",
                table: "orders",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "cancellation_requested_by",
                schema: "orders",
                table: "orders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_orders_status",
                schema: "orders",
                table: "orders",
                sql: "status IN ('draft', 'pending_reservation', 'pending_payment', 'rejected', 'cancelled', 'paid', 'payment_failed', 'payment_review', 'partially_refunded', 'cancellation_pending', 'refunded')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_orders_status",
                schema: "orders",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "cancellation_reason",
                schema: "orders",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "cancellation_requested_by",
                schema: "orders",
                table: "orders");

            migrationBuilder.AddCheckConstraint(
                name: "ck_orders_status",
                schema: "orders",
                table: "orders",
                sql: "status IN ('draft', 'pending_reservation', 'pending_payment', 'rejected', 'cancelled', 'paid', 'payment_failed', 'payment_review')");
        }
    }
}
