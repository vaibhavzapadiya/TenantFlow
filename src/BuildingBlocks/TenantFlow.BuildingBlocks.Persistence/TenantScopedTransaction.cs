using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace TenantFlow.BuildingBlocks.Persistence;

/// <summary>
/// Starts the transaction every tenant-owned unit of work runs in, with the tenant set for row-level security
/// (ADR-0011). The setting is transaction-local, so it disappears at commit or rollback and cannot leak to the next
/// user of a pooled connection. It is a backstop: callers still filter by the tenant context's id.
/// </summary>
public static class TenantScopedTransaction
{
    /// <summary>The PostgreSQL setting the row-level security policies compare <c>tenant_id</c> with.</summary>
    public const string SettingName = "app.tenant_id";

    /// <exception cref="ArgumentException">The tenant id is empty: a programming error, never a user mistake.</exception>
    public static async Task<IDbContextTransaction> BeginAsync(DatabaseFacade database, Guid tenantId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("A tenant-scoped transaction needs a tenant id.", nameof(tenantId));
        }

        var transaction = await database.BeginTransactionAsync(cancellationToken);
        try
        {
            // is_local = true: the value lives and dies with this transaction.
            await database.ExecuteSqlAsync($"SELECT set_config({SettingName}, {tenantId.ToString()}, true)", cancellationToken);
            return transaction;
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }
}
