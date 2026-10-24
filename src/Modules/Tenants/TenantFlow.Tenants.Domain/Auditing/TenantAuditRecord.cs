using TenantFlow.BuildingBlocks.Abstractions.Identity;
using TenantFlow.Tenants.Domain.Tenants;

namespace TenantFlow.Tenants.Domain.Auditing;

/// <summary>
/// Append-only record of a tenant lifecycle change made through the platform boundary (blueprint
/// section 13: audit tenant suspension and privileged operations). Never updated or deleted. A record can only
/// be made through a factory, so an action can never contradict the status change it describes.
/// </summary>
public sealed class TenantAuditRecord
{
    // Also the constructor the database mapping uses.
    private TenantAuditRecord(Guid tenantId, string action, string actorIssuer, string actorSubject, TenantStatus? oldStatus, TenantStatus newStatus, DateTimeOffset occurredAt)
    {
        TenantId = tenantId;
        Action = action;
        ActorIssuer = actorIssuer;
        ActorSubject = actorSubject;
        OldStatus = oldStatus;
        NewStatus = newStatus;
        OccurredAt = occurredAt;
    }

    public long Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string Action { get; private set; }

    public string ActorIssuer { get; private set; }

    public string ActorSubject { get; private set; }

    public TenantStatus? OldStatus { get; private set; }

    public TenantStatus NewStatus { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>A tenant was created. It starts with no previous status.</summary>
    public static TenantAuditRecord Created(Tenant tenant, ActorIdentity actor, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(actor);
        return new(tenant.Id, TenantAuditActions.Created, actor.Issuer, actor.Subject, oldStatus: null, tenant.Status, at);
    }

    /// <summary>A tenant went from active to suspended, the only transition that suspends.</summary>
    public static TenantAuditRecord Suspended(Guid tenantId, ActorIdentity actor, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return new(tenantId, TenantAuditActions.Suspended, actor.Issuer, actor.Subject, TenantStatus.Active, TenantStatus.Suspended, at);
    }

    /// <summary>A tenant went from suspended to active, the only transition that reactivates.</summary>
    public static TenantAuditRecord Reactivated(Guid tenantId, ActorIdentity actor, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return new(tenantId, TenantAuditActions.Reactivated, actor.Issuer, actor.Subject, TenantStatus.Suspended, TenantStatus.Active, at);
    }
}
