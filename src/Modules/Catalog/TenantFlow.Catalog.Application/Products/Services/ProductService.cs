using FluentValidation;
using TenantFlow.BuildingBlocks.Abstractions.Errors;
using TenantFlow.Catalog.Application.Abstractions;
using TenantFlow.Catalog.Application.Products.Commands;
using TenantFlow.Catalog.Application.Products.Dtos;
using TenantFlow.Catalog.Domain.Products;
using TenantFlow.IdentityAccess.Contracts;

namespace TenantFlow.Catalog.Application.Products.Services;

/// <summary>
/// The tenant-owned catalog use cases (SPEC-0003 criteria 5 to 10 and 17). Every one acts on the tenant in the
/// <see cref="ITenantContext"/> and takes no tenant id, so it cannot be pointed at another tenant. The route has already
/// checked the permission; what a reader sees is decided here: a caller who cannot manage the catalog sees active
/// products only, and an archived product is simply not found for them.
/// </summary>
public sealed class ProductService(
    ITenantContext context,
    ICatalogStore store,
    IValidator<CreateProductCommand> createValidator,
    IValidator<UpdateProductCommand> updateValidator,
    TimeProvider clock)
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 100;

    public async Task<ProductDetails> CreateAsync(CreateProductCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await createValidator.ValidateAndThrowAsync(command, cancellationToken);
        var now = clock.GetUtcNow();
        var product = Product.Create(Guid.CreateVersion7(now), context.TenantId, command.Sku, command.Name, command.PriceAmount, command.PriceCurrency, now);
        // Checked first so the usual duplicate is an expected answer, not a database error logged by EF Core
        // (engineering conventions section 8); a race is still decided by the unique index in the store.
        if (await store.SkuExistsAsync(product.Sku, cancellationToken))
        {
            throw new AppException(ProductErrors.SkuTaken);
        }

        return ProductDetails.From(await store.AddAsync(product, cancellationToken));
    }

    /// <exception cref="AppException"><see cref="ProductErrors.NotFound"/>: unknown, another tenant's, or hidden from this caller.</exception>
    public async Task<ProductDetails> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var record = await store.FindAsync(id, cancellationToken);
        return record is not null && IsVisible(record.Product) ? ProductDetails.From(record) : throw new AppException(ProductErrors.NotFound);
    }

    /// <param name="limit">Page size, 1 to 100.</param>
    /// <param name="cursor">The previous page's next cursor.</param>
    /// <param name="status">Only products in this status (managers only; readers always see active ones).</param>
    /// <param name="search">Words every listed product's name or SKU must contain, the last one as a prefix (SPEC-0008).</param>
    /// <param name="cancellationToken">Stops the query.</param>
    /// <exception cref="AppException">A limit, status, search or cursor error; see <see cref="ProductErrors"/>.</exception>
    public async Task<ProductPage> ListAsync(int? limit, string? cursor, string? status, string? search, CancellationToken cancellationToken)
    {
        if (search is { Length: > ProductSearch.MaxLength })
        {
            throw new AppException(ProductErrors.SearchInvalid);
        }

        string? query = null;
        if (!string.IsNullOrWhiteSpace(search) && (query = ProductSearch.ToQuery(search)) is null)
        {
            // Only punctuation: nothing can match.
            return new ProductPage([], null);
        }

        var size = limit ?? DefaultLimit;
        if (size is < 1 or > MaxLimit)
        {
            throw new AppException(ProductErrors.LimitInvalid);
        }

        ProductStatus? filter = null;
        if (status is not null)
        {
            filter = ProductStatusNames.TryParse(status, out var parsed) ? parsed : throw new AppException(ProductErrors.StatusInvalid);
        }

        var after = cursor is null ? null : ProductCursorCodec.Decode(cursor);
        // Readers who cannot manage the catalog see active products only, whatever filter they ask for.
        var effectiveFilter = CanManage() ? filter : ProductStatus.Active;
        var rows = await store.ListAsync(effectiveFilter, after, size + 1, query, cancellationToken);
        var page = rows.Take(size).Select(ProductDetails.From).ToList();
        var next = rows.Count > size ? ProductCursorCodec.Encode(page[^1]) : null;
        return new ProductPage(page, next);
    }

    /// <summary>
    /// Replaces name, price and status if the product's current version is one of <paramref name="acceptedVersions"/>
    /// (the caller's <c>If-Match</c>); null accepts any current version (<c>If-Match: *</c>).
    /// </summary>
    /// <exception cref="AppException"><see cref="ProductErrors.NotFound"/> or <see cref="ProductErrors.VersionConflict"/>.</exception>
    public async Task<ProductDetails> UpdateAsync(Guid id, UpdateProductCommand command, IReadOnlyCollection<uint>? acceptedVersions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await updateValidator.ValidateAndThrowAsync(command, cancellationToken);
        var record = await store.FindAsync(id, cancellationToken) ?? throw new AppException(ProductErrors.NotFound);
        if (acceptedVersions is not null && !acceptedVersions.Contains(record.Version))
        {
            throw new AppException(ProductErrors.VersionConflict);
        }

        record.Product.Change(command.Name, command.PriceAmount, command.PriceCurrency, command.Status, clock.GetUtcNow());
        // The store compares the version again at write time, so an edit between the read and the write still loses.
        return ProductDetails.From(await store.SaveAsync(record.Product, record.Version, cancellationToken));
    }

    private bool CanManage() => context.HasPermission(TenantPermissions.CatalogManage);

    private bool IsVisible(Product product) => CanManage() || product.Status == ProductStatus.Active;
}
