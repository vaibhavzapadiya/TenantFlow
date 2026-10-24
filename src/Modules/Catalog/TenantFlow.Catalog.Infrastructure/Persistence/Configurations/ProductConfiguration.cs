using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NpgsqlTypes;
using TenantFlow.Catalog.Domain.Products;

namespace TenantFlow.Catalog.Infrastructure.Persistence.Configurations;

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    /// <summary>The search document's shadow property (SPEC-0008 criterion 1, ADR-0024 decision 6).</summary>
    public const string SearchVector = "SearchVector";

    public void Configure(EntityTypeBuilder<Product> product)
    {
        product.ToTable("products", table =>
        {
            table.HasCheckConstraint("ck_products_status", "status IN ('active', 'archived')");
            table.HasCheckConstraint("ck_products_price_amount", "price_amount >= 0");
        });
        product.HasKey(p => p.Id).HasName("pk_products");
        product.Property(p => p.Id).HasColumnName("id").ValueGeneratedNever();
        product.Property(p => p.TenantId).HasColumnName("tenant_id");
        product.Property(p => p.Sku).HasColumnName("sku").HasMaxLength(Product.SkuMaxLength).IsRequired();
        product.Property(p => p.Name).HasColumnName("name").HasMaxLength(Product.NameMaxLength).IsRequired();
        product.Property(p => p.PriceAmount).HasColumnName("price_amount").HasPrecision(19, 4);
        product.Property(p => p.PriceCurrency).HasColumnName("price_currency").HasColumnType("character(3)").IsRequired();
        product.Property(p => p.Status).HasColumnName("status").HasConversion(ProductStatusConversion.Required).HasMaxLength(16);
        product.Property(p => p.CreatedAt).HasColumnName("created_at");
        product.Property(p => p.UpdatedAt).HasColumnName("updated_at");
        // Optimistic concurrency on PostgreSQL's row version: concurrent edits cannot both win.
        product.Property<uint>("xmin").HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        // The unique index on (tenant_id, lower(sku)) (SPEC-0003 criterion 6) is an expression index, so the migration creates it.
        product.HasIndex(p => new { p.TenantId, p.Sku, p.Id }).HasDatabaseName("ix_products_tenant_id_sku_id");
        // Name and SKU, with every run of non-alphanumerics made a space so "PLATE-1" is the words "plate" and "1" (the
        // parser would otherwise read "-1" as a number). Stored and generated, so it can never drift from the row.
        product.Property<NpgsqlTsVector>(SearchVector)
            .HasColumnName("search_vector")
            .HasComputedColumnSql("to_tsvector('simple', regexp_replace(name || ' ' || sku, '[^[:alnum:]]+', ' ', 'g'))", stored: true);
        product.HasIndex(SearchVector).HasMethod("gin").HasDatabaseName("ix_products_search_vector");
    }
}
