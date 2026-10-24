using Microsoft.EntityFrameworkCore;
using TenantFlow.BuildingBlocks.Persistence;
using TenantFlow.ImportsExports.Contracts;
using TenantFlow.ImportsExports.Domain.Jobs;

namespace TenantFlow.ImportsExports.Infrastructure.Persistence;

/// <summary>Answers <see cref="ISupportJobView"/> in a transaction scoped to that one tenant (ADR-0011).</summary>
internal sealed class SupportJobView(ImportsExportsDbContext db) : ISupportJobView
{
    public async Task<IReadOnlyList<SupportJob>> ListAsync(Guid tenantId, int limit, CancellationToken cancellationToken)
    {
        await using var transaction = await TenantScopedTransaction.BeginAsync(db.Database, tenantId, cancellationToken);
        var imports = await db.ImportJobs.AsNoTracking()
            .Where(j => j.TenantId == tenantId)
            .OrderByDescending(j => j.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);
        var exports = await db.ExportJobs.AsNoTracking()
            .Where(j => j.TenantId == tenantId)
            .OrderByDescending(j => j.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);
        return
        [
            .. imports.Select(static j => new SupportJob(j.Id, "import", j.Status.ToName(), j.CreatedCount, j.UpdatedCount, j.FailedCount, j.CheckpointRow, j.FailureCode, j.CreatedAt, j.CompletedAt))
                .Concat(exports.Select(static j => new SupportJob(j.Id, "export", j.Status.ToName(), 0, 0, 0, j.RowCount, j.FailureCode, j.CreatedAt, j.CompletedAt)))
                .OrderByDescending(static j => j.CreatedAt)
                .Take(limit),
        ];
    }
}
