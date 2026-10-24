using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TenantFlow.Subscriptions.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// SPEC-0007 slice 2 (ADR-0023 decision 1): dead-lettering for the module's due rows. A row whose handler failed 20 times
    /// in a row gets dead_at and is no longer claimed. Additive.
    /// </summary>
    public partial class AddTimerDeadLetters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "dead_at",
                schema: "subscriptions",
                table: "workflow_timers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "failures",
                schema: "subscriptions",
                table: "workflow_timers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "ck_workflow_timers_failures",
                schema: "subscriptions",
                table: "workflow_timers",
                sql: "failures >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_workflow_timers_failures",
                schema: "subscriptions",
                table: "workflow_timers");

            migrationBuilder.DropColumn(
                name: "dead_at",
                schema: "subscriptions",
                table: "workflow_timers");

            migrationBuilder.DropColumn(
                name: "failures",
                schema: "subscriptions",
                table: "workflow_timers");
        }
    }
}
