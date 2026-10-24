using TenantFlow.BuildingBlocks.Abstractions.Messaging;
using TenantFlow.Tenants.Domain.Auditing;
using TenantFlow.Tenants.Domain.Tenants;

namespace TenantFlow.Tenants.Application.Abstractions;

/// <summary>
/// Persistence port of the Tenants module (ADR-0013): specific to its use cases, not a generic
/// repository. Changes are written together, with their audit records, by <see cref="SaveChangesAsync"/>.
/// </summary>
public interface ITenantStore
{
    Task<Tenant?> FindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// True when a tenant already has this name, ignoring case. The name must already be trimmed. A read made before
    /// creating, so the common duplicate never reaches the database as an error; the unique index remains the
    /// guarantee when two creations race.
    /// </summary>
    Task<bool> NameExistsAsync(string name, CancellationToken cancellationToken);

    void Add(Tenant tenant);

    void Append(TenantAuditRecord record);

    /// <summary>
    /// Writes <paramref name="message"/> to the module's outbox for <paramref name="tenantId"/>, committed by
    /// <see cref="SaveChangesAsync"/> together with the change it announces (ADR-0021, ADR-0026).
    /// </summary>
    void Announce(OutgoingMessage message, Guid tenantId, DateTimeOffset occurredAt);

    /// <summary>Commits in one transaction.</summary>
    /// <exception cref="TenantFlow.BuildingBlocks.Abstractions.Errors.AppException">
    /// <c>tenant.concurrent_update</c> if the tenant changed meanwhile.
    /// </exception>
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
