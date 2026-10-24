using TenantFlow.Audit.Contracts;
using TenantFlow.Audit.Contracts.Events;
using TenantFlow.Tenants.Domain.Tenants;

namespace TenantFlow.Tenants.Domain.Auditing;

/// <summary>
/// The central audit events of the tenant lifecycle (SPEC-0009 criterion 2, ADR-0026), built here beside
/// <see cref="TenantAuditRecord"/> so the action can never contradict the change. The actor is a reference, never an
/// issuer or subject.
/// </summary>
public static class TenantAuditEvents
{
    public static AuditRecordedV1 Created(Tenant tenant, AuditActor actor, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        return AuditRecordedV1.Of(AuditActions.TenantCreated, actor, AuditTargetTypes.Tenant, tenant.Id, at);
    }

    public static AuditRecordedV1 Suspended(Guid tenantId, AuditActor actor, DateTimeOffset at) =>
        AuditRecordedV1.Of(AuditActions.TenantSuspended, actor, AuditTargetTypes.Tenant, tenantId, at);

    public static AuditRecordedV1 Reactivated(Guid tenantId, AuditActor actor, DateTimeOffset at) =>
        AuditRecordedV1.Of(AuditActions.TenantReactivated, actor, AuditTargetTypes.Tenant, tenantId, at);
}
