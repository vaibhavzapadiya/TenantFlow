using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TenantFlow.Payments.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// SPEC-0007 slice 2 (ADR-0023 decisions 1 and 3), expand only: a payment's subject is an order or a subscription
    /// period and attempt (order_id becomes nullable; the check allows exactly one), with one payment per attempt by a
    /// unique index; and dead-lettering for the module's due rows. Existing rows are order payments and satisfy the check.
    /// </summary>
    public partial class AddSubscriptionCharges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "dead_at",
                schema: "payments",
                table: "workflow_timers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "failures",
                schema: "payments",
                table: "workflow_timers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AlterColumn<Guid>(
                name: "order_id",
                schema: "payments",
                table: "payments",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<int>(
                name: "billing_period",
                schema: "payments",
                table: "payments",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "charge_attempt",
                schema: "payments",
                table: "payments",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "subscription_id",
                schema: "payments",
                table: "payments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_workflow_timers_failures",
                schema: "payments",
                table: "workflow_timers",
                sql: "failures >= 0");

            migrationBuilder.CreateIndex(
                name: "ux_payments_subscription_charge",
                schema: "payments",
                table: "payments",
                columns: new[] { "tenant_id", "subscription_id", "billing_period", "charge_attempt" },
                unique: true,
                filter: "subscription_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_payments_subject",
                schema: "payments",
                table: "payments",
                sql: "(order_id IS NOT NULL AND subscription_id IS NULL AND billing_period IS NULL AND charge_attempt IS NULL) OR (order_id IS NULL AND subscription_id IS NOT NULL AND billing_period >= 1 AND charge_attempt >= 1)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_workflow_timers_failures",
                schema: "payments",
                table: "workflow_timers");

            migrationBuilder.DropIndex(
                name: "ux_payments_subscription_charge",
                schema: "payments",
                table: "payments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_payments_subject",
                schema: "payments",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "dead_at",
                schema: "payments",
                table: "workflow_timers");

            migrationBuilder.DropColumn(
                name: "failures",
                schema: "payments",
                table: "workflow_timers");

            migrationBuilder.DropColumn(
                name: "billing_period",
                schema: "payments",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "charge_attempt",
                schema: "payments",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "subscription_id",
                schema: "payments",
                table: "payments");

            migrationBuilder.AlterColumn<Guid>(
                name: "order_id",
                schema: "payments",
                table: "payments",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
