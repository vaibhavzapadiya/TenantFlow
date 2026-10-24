using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TenantFlow.Payments.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// M7 review fixes R1 and R3 (ADR-0023): a refund may give back a subscription charge (order_id becomes nullable), and
    /// the one cancellation refund per payment counts only refunds that have not failed, so a failed one can be followed by
    /// another. Expand only: existing rows keep their order and no failed cancellation refund exists yet.
    /// </summary>
    public partial class ReviewFixRefunds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_refunds_one_cancellation_per_payment",
                schema: "payments",
                table: "refunds");

            migrationBuilder.AlterColumn<Guid>(
                name: "order_id",
                schema: "payments",
                table: "refunds",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.CreateIndex(
                name: "ux_refunds_one_cancellation_per_payment",
                schema: "payments",
                table: "refunds",
                column: "payment_id",
                unique: true,
                filter: "kind = 'cancellation' AND status <> 'failed'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_refunds_one_cancellation_per_payment",
                schema: "payments",
                table: "refunds");

            migrationBuilder.AlterColumn<Guid>(
                name: "order_id",
                schema: "payments",
                table: "refunds",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_refunds_one_cancellation_per_payment",
                schema: "payments",
                table: "refunds",
                column: "payment_id",
                unique: true,
                filter: "kind = 'cancellation'");
        }
    }
}
