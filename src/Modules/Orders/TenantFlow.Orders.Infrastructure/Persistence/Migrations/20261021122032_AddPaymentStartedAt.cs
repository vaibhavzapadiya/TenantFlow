using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TenantFlow.Orders.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// SPEC-0006 slice 1 (expand only): when a payment for the order began. Null for every existing order, which had none.
    /// </summary>
    public partial class AddPaymentStartedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "payment_started_at",
                schema: "orders",
                table: "orders",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "payment_started_at",
                schema: "orders",
                table: "orders");
        }
    }
}
