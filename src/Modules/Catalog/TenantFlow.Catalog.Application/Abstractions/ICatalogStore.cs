using TenantFlow.Catalog.Contracts;
using TenantFlow.Catalog.Domain.Products;

namespace TenantFlow.Catalog.Application.Abstractions;

/// <summary>
/// Storage for the Catalog use cases. Every method acts on the tenant in the current tenant context: it takes no tenant
/// id, filters by it, and runs in a transaction that sets it for row-level security (ADR-0011). With no tenant set it
/// throws before touching data.
/// </summary>
public interface ICatalogStore
{
    /// <exception cref="TenantFlow.BuildingBlocks.Abstractions.Errors.AppException"><see cref="ProductErrors.SkuTaken"/>.</exception>
    Task<ProductRecord> AddAsync(Product product, CancellationToken cancellationToken);

    Task<ProductRecord?> FindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>True when the product exists in the current tenant, whatever its status. Loads nothing.</summary>
    Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The current tenant's products among <paramref name="ids"/>, read-only; missing ids are left out.</summary>
    Task<IReadOnlyList<Product>> GetManyAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>True when the current tenant already has a product with this SKU, ignoring case.</summary>
    Task<bool> SkuExistsAsync(string sku, CancellationToken cancellationToken);

    /// <summary>
    /// Products ordered by (SKU, id), after <paramref name="after"/>, at most <paramref name="limit"/>; with
    /// <paramref name="search"/> (a <c>to_tsquery</c> text, <c>ProductSearch</c>), only those whose name or SKU match it.
    /// </summary>
    Task<IReadOnlyList<ProductRecord>> ListAsync(ProductStatus? status, ProductCursor? after, int limit, string? search, CancellationToken cancellationToken);

    /// <summary>
    /// In one transaction (SPEC-0008 criterion 5): when (<paramref name="jobId"/>, <paramref name="batchNumber"/>) was
    /// applied before, answers its stored result and changes nothing; otherwise creates or updates <paramref name="rows"/>
    /// by SKU (ignoring case; a SKU twice in one batch updates the first) and records the result with
    /// <paramref name="errors"/>.
    /// </summary>
    Task<ImportBatchResult> ApplyImportBatchAsync(Guid jobId, int batchNumber, IReadOnlyList<ProductImport> rows, IReadOnlyList<ImportRowError> errors, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Saves a changed product only if its row version is still <paramref name="expectedVersion"/>.</summary>
    /// <exception cref="TenantFlow.BuildingBlocks.Abstractions.Errors.AppException"><see cref="ProductErrors.VersionConflict"/>.</exception>
    Task<ProductRecord> SaveAsync(Product product, uint expectedVersion, CancellationToken cancellationToken);
}
