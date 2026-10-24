using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TenantFlow.Orders.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// SPEC-0006 slice 3 (expand only): the order states the payment saga reaches (the status CHECK only widens, so every
    /// stored row stays valid), and the saga consumer's processed-message table with forced row-level security
    /// (ADR-0011, ADR-0021, ADR-0022).
    /// </summary>
    public partial class AddPaymentStatesAndProcessedMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_orders_status",
                schema: "orders",
                table: "orders");

            migrationBuilder.CreateTable(
                name: "processed_messages",
                schema: "orders",
                columns: table => new
                {
                    consumer = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_processed_messages", x => new { x.consumer, x.message_id });
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_orders_status",
                schema: "orders",
                table: "orders",
                sql: "status IN ('draft', 'pending_reservation', 'pending_payment', 'rejected', 'cancelled', 'paid', 'payment_failed', 'payment_review')");

            // Row-level security (ADR-0011, ADR-0019): the same fail-closed policy as the other tenant-owned tables.
            migrationBuilder.Sql("ALTER TABLE orders.processed_messages ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE orders.processed_messages FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("""
                CREATE POLICY tenant_isolation ON orders.processed_messages
                    USING (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "processed_messages",
                schema: "orders");

            migrationBuilder.DropCheckConstraint(
                name: "ck_orders_status",
                schema: "orders",
                table: "orders");

            migrationBuilder.AddCheckConstraint(
                name: "ck_orders_status",
                schema: "orders",
                table: "orders",
                sql: "status IN ('draft', 'pending_reservation', 'pending_payment', 'rejected', 'cancelled')");
        }
    }
}
