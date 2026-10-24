using TenantFlow.Audit.Contracts;
using TenantFlow.Audit.Contracts.Events;
using TenantFlow.BuildingBlocks.Abstractions.Errors;
using TenantFlow.BuildingBlocks.Abstractions.Messaging;
using TenantFlow.IdentityAccess.Contracts;
using TenantFlow.ImportsExports.Contracts;
using TenantFlow.Operations.Application.Abstractions;
using TenantFlow.Operations.Application.Observability;
using TenantFlow.Operations.Domain.SupportGrants;
using TenantFlow.Tenants.Contracts;

namespace TenantFlow.Operations.Application.SupportGrants;

/// <summary>
/// Support grants and the read-only support views (SPEC-0009 criteria 16 to 18, ADR-0027). The route has checked the
/// caller's platform role. Every view first commits the access record against the caller's own active grant for that
/// tenant, then reads; with no such grant the answer is the same 404 as for an unknown tenant, and nothing is read. No
/// use case here writes tenant data.
/// </summary>
public sealed class SupportService(
    ISupportGrantStore store,
    ITenantDirectory tenants,
    ISupportMemberView members,
    ISupportJobView jobs,
    OperationsMetrics metrics,
    TimeProvider clock)
{
    /// <summary>The most jobs a view returns.</summary>
    public const int JobLimit = 50;

    /// <exception cref="AppException">
    /// <see cref="OperationsErrors.TenantRequired"/>, <see cref="OperationsErrors.TicketInvalid"/>,
    /// <see cref="OperationsErrors.DurationInvalid"/>, <see cref="TenantContractErrors.NotFound"/> or
    /// <see cref="OperationsErrors.GrantExists"/>.
    /// </exception>
    public async Task<SupportGrant> OpenAsync(Guid? tenantId, string? ticket, int? minutes, AuditActor supportOperator, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(supportOperator);
        if (tenantId is not { } tenant || tenant == Guid.Empty)
        {
            throw new AppException(OperationsErrors.TenantRequired);
        }

        if (SupportGrant.Check(ticket, minutes) is { } invalid)
        {
            throw new AppException(invalid);
        }

        if (!(await tenants.GetAvailabilityAsync([tenant], cancellationToken)).ContainsKey(tenant))
        {
            throw new AppException(TenantContractErrors.NotFound);
        }

        var now = clock.GetUtcNow();
        var grant = SupportGrant.Open(Guid.CreateVersion7(now), tenant, supportOperator.Reference, ticket!, minutes!.Value, now);
        if (!await store.TryOpenAsync(grant, Audit(AuditActions.SupportGrantOpened, supportOperator, grant.Id, now), now, cancellationToken))
        {
            throw new AppException(OperationsErrors.GrantExists);
        }

        metrics.RecordGrantOpened();
        return grant;
    }

    /// <summary>Ends a grant: its own operator may, and a platform administrator may revoke any (criterion 18).</summary>
    /// <exception cref="AppException"><see cref="OperationsErrors.GrantNotFound"/> for an unknown, foreign or inactive grant.</exception>
    public async Task EndAsync(Guid grantId, AuditActor caller, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var grant = await store.FindAsync(grantId, cancellationToken);
        if (grant is null || (caller.Kind != AuditActorKinds.PlatformAdmin && grant.OperatorRef != caller.Reference))
        {
            throw new AppException(OperationsErrors.GrantNotFound);
        }

        var now = clock.GetUtcNow();
        if (!await store.EndAsync(grant.Id, caller.Reference, now, Audit(AuditActions.SupportGrantEnded, caller, grant.Id, now), cancellationToken))
        {
            throw new AppException(OperationsErrors.GrantNotFound);
        }
    }

    /// <exception cref="AppException"><see cref="TenantContractErrors.NotFound"/> without an active grant of the caller.</exception>
    public async Task<IReadOnlyList<SupportMember>> MembersAsync(Guid tenantId, AuditActor supportOperator, CancellationToken cancellationToken)
    {
        await AccessAsync(tenantId, supportOperator, cancellationToken);
        return await members.ListAsync(tenantId, cancellationToken);
    }

    /// <exception cref="AppException"><see cref="TenantContractErrors.NotFound"/> without an active grant of the caller.</exception>
    public async Task<IReadOnlyList<SupportJob>> JobsAsync(Guid tenantId, AuditActor supportOperator, CancellationToken cancellationToken)
    {
        await AccessAsync(tenantId, supportOperator, cancellationToken);
        return await jobs.ListAsync(tenantId, JobLimit, cancellationToken);
    }

    // The access record is committed before anything is read; no grant, no record, no read.
    private async Task AccessAsync(Guid tenantId, AuditActor supportOperator, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(supportOperator);
        var now = clock.GetUtcNow();
        var grant = await store.RecordAccessAsync(
            tenantId, supportOperator.Reference, now, grant => Audit(AuditActions.SupportTenantAccessed, supportOperator, grant.Id, now), cancellationToken);
        if (grant is null)
        {
            throw new AppException(TenantContractErrors.NotFound);
        }
    }

    private static OutgoingMessage Audit(string action, AuditActor actor, Guid grantId, DateTimeOffset now) =>
        OutgoingMessage.From(AuditRecordedV1.Of(action, actor, AuditTargetTypes.SupportGrant, grantId, now));
}
