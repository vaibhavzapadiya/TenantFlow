using Microsoft.EntityFrameworkCore;
using Npgsql;
using TenantFlow.BuildingBlocks.Abstractions.Errors;
using TenantFlow.BuildingBlocks.Abstractions.Messaging;
using TenantFlow.BuildingBlocks.Messaging.Outbox;
using TenantFlow.Tenants.Application.Abstractions;
using TenantFlow.Tenants.Domain.Auditing;
using TenantFlow.Tenants.Domain.Tenants;

namespace TenantFlow.Tenants.Infrastructure.Persistence;

internal sealed class TenantStore(TenantsDbContext context) : ITenantStore
{
    // The unique index on lower(name), created by the AddUniqueTenantName migration (SPEC-0002 criterion 26).
    internal const string NameIndex = "ux_tenants_name_lower";


    public Task<Tenant?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        context.Tenants.SingleOrDefaultAsync(tenant => tenant.Id == id, cancellationToken);

    // ILIKE with every wildcard escaped is a case-insensitive equality. The unique index on lower(name) is the guarantee;
    // this read only keeps the usual duplicate from reaching the database as an error.
    public Task<bool> NameExistsAsync(string name, CancellationToken cancellationToken) =>
        context.Tenants.AnyAsync(tenant => EF.Functions.ILike(tenant.Name, EscapeLike(name), "\\"), cancellationToken);

    public void Add(Tenant tenant) => context.Tenants.Add(tenant);

    public void Append(TenantAuditRecord record) => context.AuditRecords.Add(record);

    // Tracked like the change itself, so the one SaveChanges below writes both or neither (ADR-0021).
    public void Announce(OutgoingMessage message, Guid tenantId, DateTimeOffset occurredAt) =>
        context.OutboxMessages.Add(OutboxMessage.Create(message, tenantId, occurredAt));

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            // One SaveChanges is one transaction: the change and its audit record commit together.
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new AppException(TenantErrors.ConcurrentUpdate, exception);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: NameIndex })
        {
            // Two creations of one name: the index decides, so concurrent requests have exactly one winner. Nothing
            // was written (the tenant and its audit record are one transaction), and the message names no tenant.
            throw new AppException(TenantErrors.NameTaken, exception);
        }
    }

    private static string EscapeLike(string value) =>
        value.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("%", @"\%", StringComparison.Ordinal).Replace("_", @"\_", StringComparison.Ordinal);
}
