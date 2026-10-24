namespace TenantFlow.Catalog.Contracts;

/// <summary>
/// The size limits of the product data another module may copy (<see cref="ProductSnapshot"/>). The Catalog domain
/// enforces them, and a module that stores a snapshot sizes its columns from them, so a value Catalog accepts always fits.
/// Widening one is a contract change: every module storing it needs a migration in the same change.
/// </summary>
public static class ProductLimits
{
    public const int SkuMaxLength = 64;
    public const int NameMaxLength = 200;
}
