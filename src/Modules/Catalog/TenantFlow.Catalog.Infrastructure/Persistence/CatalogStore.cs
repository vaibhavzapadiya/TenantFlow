using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using TenantFlow.BuildingBlocks.Abstractions.Errors;
using TenantFlow.BuildingBlocks.Persistence;
using TenantFlow.Catalog.Application.Abstractions;
using TenantFlow.Catalog.Contracts;
using TenantFlow.Catalog.Domain.Products;
using TenantFlow.Catalog.Infrastructure.Persistence.Configurations;
using TenantFlow.IdentityAccess.Contracts;

namespace TenantFlow.Catalog.Infrastructure.Persistence;

/// <summary>
/// Every operation filters by the tenant context's id and runs in a transaction that sets it for row-level security
/// (ADR-0011): two independent controls. Reading <c>context.TenantId</c> throws when no tenant is set, so nothing here
/// touches data without one.
/// </summary>
internal sealed class CatalogStore(CatalogDbContext db, ITenantContext tenant) : ICatalogStore
{
    // The unique index on (tenant_id, lower(sku)), created by the InitialSchema migration (SPEC-0003 criterion 6).
    internal const string SkuIndex = "ux_products_tenant_id_sku_lower";

    private const string VersionColumn = "xmin";

    // A placeholder until the batch's result is known, in the same transaction.
    private const string EmptyResult = "{}";

    private static readonly JsonSerializerOptions BatchJson = new(JsonSerializerDefaults.Web);

    public async Task<ProductRecord> AddAsync(Product product, CancellationToken cancellationToken)
    {
        if (product.TenantId != tenant.TenantId)
        {
            // A programming error: a product is created for the caller's tenant only.
            throw new InvalidOperationException("A product can only be added to the tenant in the current context.");
        }

        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenant.TenantId, cancellationToken);
        db.Products.Add(product);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: SkuIndex })
        {
            // Two creations of one SKU: the index decides, so concurrent requests have exactly one winner.
            throw new AppException(ProductErrors.SkuTaken, exception);
        }

        await transaction.CommitAsync(cancellationToken);
        return Record(product);
    }

    public async Task<ProductRecord?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        var product = await db.Products.SingleOrDefaultAsync(p => p.TenantId == tenantId && p.Id == id, cancellationToken);
        return product is null ? null : Record(product);
    }

    // Matches the unique index on (tenant_id, lower(sku)); the index is the guarantee, this read keeps the usual
    // duplicate from reaching the database as an error.
    public async Task<bool> SkuExistsAsync(string sku, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        // ILIKE with every wildcard escaped is a case-insensitive equality, as TenantStore does for tenant names.
        return await db.Products.AnyAsync(
            p => p.TenantId == tenantId && EF.Functions.ILike(p.Sku, EscapeLike(sku), "\\"), cancellationToken);
    }

    public async Task<IReadOnlyList<ProductRecord>> ListAsync(ProductStatus? status, ProductCursor? after, int limit, string? search, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        // Read-only: no tracking; the row version (xmin) is projected next to each product instead.
        var query = db.Products.AsNoTracking().Where(p => p.TenantId == tenantId);
        if (status is not null)
        {
            query = query.Where(p => p.Status == status.Value);
        }

        if (search is not null)
        {
            // The stored search document and its GIN index ix_products_search_vector (SPEC-0008 criterion 1).
            query = query.Where(p => EF.Property<NpgsqlTsVector>(p, ProductConfiguration.SearchVector).Matches(EF.Functions.ToTsQuery("simple", search)));
        }

        if (after is not null)
        {
            // Keyset: strictly after (sku, id) in the index's own order. Not an OFFSET, so inserts between pages shift nothing.
            // A row-value comparison, (sku, id) > (@sku, @id), lets PostgreSQL seek on ix_products_tenant_id_sku_id;
            // the equivalent OR of two conditions cannot use the index as a range.
            var (afterSku, afterId) = (after.Sku, after.Id);
            query = query.Where(p => EF.Functions.GreaterThan(ValueTuple.Create(p.Sku, p.Id), ValueTuple.Create(afterSku, afterId)));
        }

        var rows = await query.OrderBy(p => p.Sku).ThenBy(p => p.Id).Take(limit)
            .Select(p => new { Product = p, Version = EF.Property<uint>(p, VersionColumn) })
            .ToListAsync(cancellationToken);
        return [.. rows.Select(static row => new ProductRecord(row.Product, row.Version))];
    }

    public async Task<IReadOnlyList<Product>> GetManyAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        return await db.Products.AsNoTracking().Where(p => p.TenantId == tenantId && ids.Contains(p.Id)).ToListAsync(cancellationToken);
    }

    public async Task<bool> ExistsAsync(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        return await db.Products.AnyAsync(p => p.TenantId == tenantId && p.Id == id, cancellationToken);
    }

    public async Task<ImportBatchResult> ApplyImportBatchAsync(Guid jobId, int batchNumber, IReadOnlyList<ProductImport> rows, IReadOnlyList<ImportRowError> errors, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(errors);
        var tenantId = tenant.TenantId;
        db.ChangeTracker.Clear();
        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        // The batch first: of two runs of one batch exactly one inserts it; the other waits here, then replays its result.
        var applied = await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO catalog.import_batches (tenant_id, job_id, batch_number, result, applied_at)
            VALUES ({tenantId}, {jobId}, {batchNumber}, {EmptyResult}::jsonb, {now})
            ON CONFLICT (tenant_id, job_id, batch_number) DO NOTHING
            """,
            cancellationToken);
        if (applied == 0)
        {
            var stored = await db.Database.SqlQuery<string>(
                $"SELECT result::text AS \"Value\" FROM catalog.import_batches WHERE tenant_id = {tenantId} AND job_id = {jobId} AND batch_number = {batchNumber}")
                .SingleAsync(cancellationToken);
            return JsonSerializer.Deserialize<ImportBatchResult>(stored, BatchJson)!;
        }

        var skus = rows.Select(static r => r.Sku.ToLowerInvariant()).Distinct().ToList();
        var skuArray = skus.ToArray();
        var existing = (await db.Products.FromSql($"SELECT *, xmin FROM catalog.products WHERE tenant_id = {tenantId} AND lower(sku) = ANY({skuArray})").ToListAsync(cancellationToken))
            .ToDictionary(static p => p.Sku.ToLowerInvariant());
        var (created, updated) = (0, 0);
        foreach (var row in rows)
        {
            if (existing.TryGetValue(row.Sku.ToLowerInvariant(), out var product))
            {
                product.Change(row.Name, row.PriceAmount, row.PriceCurrency, row.Status, now);
                updated++;
            }
            else
            {
                product = Product.Create(Guid.CreateVersion7(now), tenantId, row.Sku, row.Name, row.PriceAmount, row.PriceCurrency, now);
                if (row.Status != ProductStatusNames.Active)
                {
                    product.Change(row.Name, row.PriceAmount, row.PriceCurrency, row.Status, now);
                }

                db.Products.Add(product);
                existing[row.Sku.ToLowerInvariant()] = product;
                created++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        var result = new ImportBatchResult(created, updated, errors);
        var json = JsonSerializer.Serialize(result, BatchJson);
        await db.Database.ExecuteSqlAsync(
            $"UPDATE catalog.import_batches SET result = {json}::jsonb WHERE tenant_id = {tenantId} AND job_id = {jobId} AND batch_number = {batchNumber}",
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        db.ChangeTracker.Clear();
        return result;
    }

    public async Task<ProductRecord> SaveAsync(Product product, uint expectedVersion, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        if (product.TenantId != tenantId)
        {
            throw new InvalidOperationException("A product can only be saved by the tenant that owns it.");
        }

        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        // The product is tracked from FindAsync in this scope, so SaveChanges writes exactly what Change modified. The
        // compare-and-set uses the version the caller read, whatever the row holds now.
        db.Entry(product).Property<uint>(VersionColumn).OriginalValue = expectedVersion;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new AppException(ProductErrors.VersionConflict, exception);
        }

        await transaction.CommitAsync(cancellationToken);
        return Record(product);
    }

    private ProductRecord Record(Product product) => new(product, db.Entry(product).Property<uint>(VersionColumn).CurrentValue);

    private static string EscapeLike(string value) =>
        value.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("%", @"\%", StringComparison.Ordinal).Replace("_", @"\_", StringComparison.Ordinal);
}
