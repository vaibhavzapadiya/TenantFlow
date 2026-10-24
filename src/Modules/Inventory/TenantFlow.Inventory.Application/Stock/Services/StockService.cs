using FluentValidation;
using TenantFlow.BuildingBlocks.Abstractions.Errors;
using TenantFlow.Catalog.Contracts;
using TenantFlow.IdentityAccess.Contracts;
using TenantFlow.Inventory.Application.Abstractions;
using TenantFlow.Inventory.Application.Stock.Commands;
using TenantFlow.Inventory.Application.Stock.Dtos;
using TenantFlow.Inventory.Domain.Stock;

namespace TenantFlow.Inventory.Application.Stock.Services;

/// <summary>
/// The tenant-owned stock use cases (SPEC-0003 criteria 11 to 16). Every one acts on the tenant in the
/// <see cref="ITenantContext"/> and takes no tenant id. A product is known only through the Catalog contract, which
/// answers for the current tenant only, so another tenant's product is "not found" and no balance row is ever created
/// for it. The route has already checked the permission.
/// </summary>
public sealed class StockService(
    ITenantContext context,
    IProductCatalog catalog,
    IInventoryStore store,
    IValidator<AdjustStockCommand> validator,
    TimeProvider clock)
{
    /// <exception cref="AppException"><see cref="CatalogContractErrors.ProductNotFound"/>.</exception>
    public async Task<StockDetails> GetAsync(Guid productId, CancellationToken cancellationToken)
    {
        await EnsureProductAsync(productId, cancellationToken);
        return Details(productId, await store.GetLevelAsync(productId, cancellationToken));
    }

    /// <summary>Adds a signed delta to the product's stock, never taking it below zero or below what orders hold.</summary>
    /// <exception cref="AppException">
    /// <see cref="CatalogContractErrors.ProductNotFound"/>, or <see cref="InventoryErrors.InsufficientStock"/> when the
    /// balance would go below zero or below the reserved units (the balance is then unchanged).
    /// </exception>
    public async Task<StockDetails> AdjustAsync(Guid productId, AdjustStockCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await validator.ValidateAndThrowAsync(command, cancellationToken);
        await EnsureProductAsync(productId, cancellationToken);

        var adjustment = StockAdjustment.Record(context.TenantId, productId, command.Delta, command.Reason, context.MembershipId, clock.GetUtcNow());
        var level = await store.TryApplyAsync(adjustment, cancellationToken) ?? throw new AppException(InventoryErrors.InsufficientStock);
        return Details(productId, level);
    }

    private static StockDetails Details(Guid productId, StockLevel level) => new(productId, level.OnHand, level.Reserved, level.Available);

    private async Task EnsureProductAsync(Guid productId, CancellationToken cancellationToken)
    {
        if (!await catalog.ExistsAsync(productId, cancellationToken))
        {
            throw new AppException(CatalogContractErrors.ProductNotFound);
        }
    }
}
