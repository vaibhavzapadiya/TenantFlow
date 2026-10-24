using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace TenantFlow.Catalog.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// SPEC-0008 criterion 1 (ADR-0024 decision 6): a stored generated search document over name and SKU (`simple`
    /// configuration, punctuation made spaces) with a GIN index. Additive; existing rows get theirs when it is added.
    /// </summary>
    public partial class AddProductSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<NpgsqlTsVector>(
                name: "search_vector",
                schema: "catalog",
                table: "products",
                type: "tsvector",
                nullable: true,
                computedColumnSql: "to_tsvector('simple', regexp_replace(name || ' ' || sku, '[^[:alnum:]]+', ' ', 'g'))",
                stored: true);

            migrationBuilder.CreateIndex(
                name: "ix_products_search_vector",
                schema: "catalog",
                table: "products",
                column: "search_vector")
                .Annotation("Npgsql:IndexMethod", "gin");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_products_search_vector",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropColumn(
                name: "search_vector",
                schema: "catalog",
                table: "products");
        }
    }
}
