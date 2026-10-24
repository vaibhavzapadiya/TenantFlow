namespace TenantFlow.Catalog.Domain.Products;

/// <summary>
/// The stable names a <see cref="ProductStatus"/> has outside the domain: the database and the API both use these,
/// so the mapping exists once. A stored name nobody recognises is data corruption and fails loudly; a name a caller
/// sends is input, checked with <see cref="TryParse"/>.
/// </summary>
public static class ProductStatusNames
{
    public const string Active = "active";
    public const string Archived = "archived";

    /// <exception cref="InvalidOperationException">The status is not one the domain defines.</exception>
    public static string ToName(this ProductStatus status) => status switch
    {
        ProductStatus.Active => Active,
        ProductStatus.Archived => Archived,
        _ => throw new InvalidOperationException("The product status is not one the domain defines."),
    };

    /// <exception cref="InvalidOperationException">The stored name is not a known status name (case matters).</exception>
    public static ProductStatus Parse(string name) =>
        TryParse(name, out var status) ? status : throw new InvalidOperationException("The stored product status is not a known status name.");

    /// <summary>Reads a status name from input; false for anything that is not exactly a known name.</summary>
    public static bool TryParse(string? name, out ProductStatus status)
    {
        status = name switch
        {
            Active => ProductStatus.Active,
            Archived => ProductStatus.Archived,
            _ => default,
        };
        return status != default;
    }
}
