using Microsoft.EntityFrameworkCore;
using TenantFlow.BuildingBlocks.Persistence;
using TenantFlow.IdentityAccess.Contracts;
using TenantFlow.ImportsExports.Application.Abstractions;
using TenantFlow.ImportsExports.Domain.Files;

namespace TenantFlow.ImportsExports.Infrastructure.Persistence;

/// <summary>
/// Every operation filters by the tenant context's id and runs in a transaction that sets it for row-level security
/// (ADR-0011): two independent controls.
/// </summary>
internal sealed class FileStore(ImportsExportsDbContext db, ITenantContext tenant) : IFileStore
{
    public async Task AddAsync(StoredFile file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        var tenantId = tenant.TenantId;
        if (file.TenantId != tenantId)
        {
            throw new InvalidOperationException("A file can only be added in the tenant in the current context.");
        }

        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        db.Files.Add(file);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        db.ChangeTracker.Clear();
    }

    public async Task<StoredFile?> FindAsync(Guid fileId, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        return await db.Files.AsNoTracking().SingleOrDefaultAsync(f => f.TenantId == tenantId && f.Id == fileId, cancellationToken);
    }

    public async Task MarkWrittenAsync(Guid fileId, StoredObject written, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(written);
        var tenantId = tenant.TenantId;
        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        await db.Database.ExecuteSqlAsync(
            $"UPDATE imports_exports.stored_files SET size = {written.Size}, sha256 = {written.Sha256} WHERE tenant_id = {tenantId} AND id = {fileId}",
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task MarkDeletedAsync(Guid fileId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        await db.Database.ExecuteSqlAsync(
            $"UPDATE imports_exports.stored_files SET deleted_at = {now} WHERE tenant_id = {tenantId} AND id = {fileId} AND deleted_at IS NULL",
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
