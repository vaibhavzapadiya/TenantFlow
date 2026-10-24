namespace TenantFlow.Catalog.Domain.Products;

/// <summary>Publication state of a product (SPEC-0003 criteria 8 and 9). Customers see active products only.</summary>
public enum ProductStatus
{
    Active = 1,
    Archived = 2,
}
