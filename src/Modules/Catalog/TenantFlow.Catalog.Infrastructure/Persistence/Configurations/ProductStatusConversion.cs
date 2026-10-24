using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using TenantFlow.Catalog.Domain.Products;

namespace TenantFlow.Catalog.Infrastructure.Persistence.Configurations;

/// <summary>
/// Stores <see cref="ProductStatus"/> by its stable name, never as an enum number. The names and the strict parse
/// belong to the domain (<see cref="ProductStatusNames"/>): a stored value nobody recognises fails loudly.
/// </summary>
internal static class ProductStatusConversion
{
    public static readonly ValueConverter<ProductStatus, string> Required =
        new(status => status.ToName(), name => ProductStatusNames.Parse(name));
}
