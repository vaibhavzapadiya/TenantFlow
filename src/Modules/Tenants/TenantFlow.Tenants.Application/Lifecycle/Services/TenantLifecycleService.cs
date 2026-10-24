using FluentValidation;
using TenantFlow.Audit.Contracts;
using TenantFlow.Audit.Contracts.Events;
using TenantFlow.BuildingBlocks.Abstractions.Errors;
using TenantFlow.BuildingBlocks.Abstractions.Identity;
using TenantFlow.BuildingBlocks.Abstractions.Messaging;
using TenantFlow.Tenants.Application.Abstractions;
using TenantFlow.Tenants.Application.Lifecycle.Commands;
using TenantFlow.Tenants.Application.Lifecycle.Dtos;
using TenantFlow.Tenants.Domain.Auditing;
using TenantFlow.Tenants.Domain.Tenants;

namespace TenantFlow.Tenants.Application.Lifecycle.Services;

/// <summary>
/// Tenant lifecycle use cases for the platform boundary (SPEC-0002 criteria 24 and 25; ADR-0010).
/// Callers have already authorised the platform operator; every change is audited in the same commit, both in the
/// module's own audit table and as an <c>audit.recorded.v1</c> event in its outbox (SPEC-0009 criterion 2, ADR-0026).
/// </summary>
/// <remarks>
/// <c>actor</c> parameters are the operator's identity for the module's own audit table;
/// <c>auditActor</c> parameters are the reference the central audit log stores instead of that identity.
/// </remarks>
public sealed class TenantLifecycleService(ITenantStore store, IValidator<CreateTenantCommand> validator, TimeProvider clock)
{
    public async Task<TenantDetails> CreateAsync(CreateTenantCommand command, ActorIdentity actor, AuditActor auditActor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(auditActor);
        await validator.ValidateAndThrowAsync(command, cancellationToken);

        // Names are unique ignoring case and surrounding whitespace (SPEC-0002 criterion 26). Checked first so the
        // usual duplicate is an expected answer, not a database error; a race is caught by the index in the store.
        if (await store.NameExistsAsync(command.Name!.Trim(), cancellationToken))
        {
            throw new AppException(TenantErrors.NameTaken);
        }

        var now = clock.GetUtcNow();
        var tenant = Tenant.Create(Guid.CreateVersion7(now), command.Name!, now);
        store.Add(tenant);
        store.Append(TenantAuditRecord.Created(tenant, actor, now));
        Announce(TenantAuditEvents.Created(tenant, auditActor, now), tenant.Id, now);
        await store.SaveChangesAsync(cancellationToken);
        return TenantDetails.From(tenant);
    }

    /// <exception cref="AppException"><see cref="TenantErrors.NotFound"/> when the tenant does not exist.</exception>
    public async Task<TenantDetails> GetAsync(Guid id, CancellationToken cancellationToken) =>
        TenantDetails.From(await store.FindAsync(id, cancellationToken) ?? throw new AppException(TenantErrors.NotFound));

    /// <exception cref="AppException">
    /// <see cref="TenantErrors.NotFound"/>, or <see cref="TenantErrors.AlreadySuspended"/> when the tenant is not active.
    /// </exception>
    public Task<TenantDetails> SuspendAsync(Guid id, ActorIdentity actor, AuditActor auditActor, CancellationToken cancellationToken) =>
        ChangeStatusAsync(id, actor, auditActor, static (tenant, now) => tenant.Suspend(now), TenantAuditRecord.Suspended, TenantAuditEvents.Suspended, cancellationToken);

    /// <exception cref="AppException">
    /// <see cref="TenantErrors.NotFound"/>, or <see cref="TenantErrors.NotSuspended"/> when the tenant is not suspended.
    /// </exception>
    public Task<TenantDetails> ReactivateAsync(Guid id, ActorIdentity actor, AuditActor auditActor, CancellationToken cancellationToken) =>
        ChangeStatusAsync(id, actor, auditActor, static (tenant, now) => tenant.Reactivate(now), TenantAuditRecord.Reactivated, TenantAuditEvents.Reactivated, cancellationToken);

    private async Task<TenantDetails> ChangeStatusAsync(
        Guid id,
        ActorIdentity actor,
        AuditActor auditActor,
        Action<Tenant, DateTimeOffset> change,
        Func<Guid, ActorIdentity, DateTimeOffset, TenantAuditRecord> audit,
        Func<Guid, AuditActor, DateTimeOffset, AuditRecordedV1> auditEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(auditActor);
        var tenant = await store.FindAsync(id, cancellationToken) ?? throw new AppException(TenantErrors.NotFound);

        // The domain decides what the change means for the audit; the service only keeps the pieces together.
        var now = clock.GetUtcNow();
        change(tenant, now);
        store.Append(audit(tenant.Id, actor, now));
        Announce(auditEvent(tenant.Id, auditActor, now), tenant.Id, now);
        await store.SaveChangesAsync(cancellationToken);
        return TenantDetails.From(tenant);
    }

    private void Announce(AuditRecordedV1 auditEvent, Guid tenantId, DateTimeOffset now) =>
        store.Announce(OutgoingMessage.From(auditEvent), tenantId, now);
}
