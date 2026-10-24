using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TenantFlow.Subscriptions.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// M7 review fix R9 (ADR-0023): a subscription of a removed member is cancelled with reason member_removed. The check
    /// only widens.
    /// </summary>
    public partial class AddMemberRemovedReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_subscriptions_cancel_reason",
                schema: "subscriptions",
                table: "subscriptions");

            migrationBuilder.AddCheckConstraint(
                name: "ck_subscriptions_cancel_reason",
                schema: "subscriptions",
                table: "subscriptions",
                sql: "cancel_reason IS NULL OR cancel_reason IN ('requested', 'payment_failed', 'member_removed')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_subscriptions_cancel_reason",
                schema: "subscriptions",
                table: "subscriptions");

            migrationBuilder.AddCheckConstraint(
                name: "ck_subscriptions_cancel_reason",
                schema: "subscriptions",
                table: "subscriptions",
                sql: "cancel_reason IS NULL OR cancel_reason IN ('requested', 'payment_failed')");
        }
    }
}
