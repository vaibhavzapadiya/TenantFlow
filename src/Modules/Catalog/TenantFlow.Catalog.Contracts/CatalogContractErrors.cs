using TenantFlow.BuildingBlocks.Abstractions.Errors;

namespace TenantFlow.Catalog.Contracts;

/// <summary>
/// The errors about a product that other modules answer with (ADR-0016), defined once so "no such product" has one
/// code and one message whichever module notices it. The Catalog module's own catalog reuses <see cref="ProductNotFound"/>.
/// </summary>
public static class CatalogContractErrors
{
    public static readonly AppError ProductNotFound =
        new("catalog.product_not_found", ErrorKind.NotFound, "The product does not exist.");
}
