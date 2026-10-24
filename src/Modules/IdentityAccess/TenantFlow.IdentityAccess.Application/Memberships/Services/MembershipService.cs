using FluentValidation;
using TenantFlow.Audit.Contracts;
using TenantFlow.BuildingBlocks.Abstractions.Errors;
using TenantFlow.BuildingBlocks.Abstractions.Identity;
using TenantFlow.BuildingBlocks.Abstractions.Messaging;
using TenantFlow.IdentityAccess.Application.Abstractions;
using TenantFlow.IdentityAccess.Application.Memberships.Commands;
using TenantFlow.IdentityAccess.Application.Memberships.Dtos;
using TenantFlow.IdentityAccess.Domain.Auditing;
using TenantFlow.IdentityAccess.Domain.Memberships;
using TenantFlow.Tenants.Contracts;

namespace TenantFlow.IdentityAccess.Application.Memberships.Services;

/// <summary>
/// Membership use cases (SPEC-0002 criteria 16 and 19 to 21). Callers have already authorised the actor (the platform
/// boundary, or a tenant administrator in S6); every change here is audited in the same commit as the change, in the
/// module's own audit table (<c>actor</c>) and as an <c>audit.recorded.v1</c> outbox event that names the actor by
/// reference only (<c>auditActor</c>; SPEC-0009 criterion 2, ADR-0026). Each
/// change runs inside one tenant's serialized change (<see cref="IMembershipStore.BeginChangeAsync"/>), so the
/// last-administrator rule holds under concurrency.
/// </summary>
public sealed class MembershipService(
    IMembershipStore store,
    ITenantDirectory tenants,
    IValidator<AddMemberCommand> addValidator,
    IValidator<ChangeRoleCommand> changeRoleValidator,
    TimeProvider clock)
{
    /// <exception cref="AppException"><see cref="MembershipErrors.AlreadyExists"/> when the identity is already a member.</exception>
    public async Task<MembershipDetails> AddAsync(AddMemberCommand command, ActorIdentity actor, AuditActor auditActor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(auditActor);
        await addValidator.ValidateAndThrowAsync(command, cancellationToken);
        return await AddValidatedAsync(command, actor, auditActor, cancellationToken);
    }

    /// <summary>
    /// Makes a user an administrator of a tenant on behalf of the platform boundary (SPEC-0002 criterion 24). Unlike
    /// <see cref="AddAsync"/> it checks the tenant first: it must exist and be active.
    /// </summary>
    /// <exception cref="AppException">
    /// <see cref="TenantContractErrors.NotFound"/>, <see cref="TenantContractErrors.Suspended"/>, or
    /// <see cref="MembershipErrors.AlreadyExists"/> when the user already belongs to the tenant.
    /// </exception>
    public async Task<MembershipDetails> AddTenantAdministratorAsync(
        Guid tenantGuid,
        string? issuer,
        string? subject,
        ActorIdentity actor,
        AuditActor auditActor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(auditActor);
        var command = new AddMemberCommand(tenantGuid, issuer, subject, MembershipRoleNames.TenantAdmin);
        await addValidator.ValidateAndThrowAsync(command, cancellationToken);
        await EnsureTenantAcceptsChangesAsync(tenantGuid, cancellationToken);
        return await AddValidatedAsync(command, actor, auditActor, cancellationToken);
    }

    private async Task<MembershipDetails> AddValidatedAsync(AddMemberCommand command, ActorIdentity actor, AuditActor auditActor, CancellationToken cancellationToken)
    {
        var tenantId = TenantIdOrNotFound(command.TenantId);

        await using var change = await store.BeginChangeAsync(tenantId, cancellationToken);
        if (await change.FindActiveAsync(command.Issuer!, command.Subject!, cancellationToken) is not null)
        {
            throw new AppException(MembershipErrors.AlreadyExists);
        }

        var now = clock.GetUtcNow();
        var membership = Membership.Create(
            new MembershipId(Guid.CreateVersion7(now)),
            tenantId,
            new ActorIdentity(command.Issuer!, command.Subject!),
            MembershipRoleNames.Parse(command.Role!),
            now);
        change.Add(membership);
        change.Append(MembershipAuditRecord.Added(membership, actor, now));
        change.Announce(OutgoingMessage.From(MembershipAuditEvents.Added(membership, auditActor, now)), now);
        await change.CommitAsync(cancellationToken);
        return MembershipDetails.From(membership);
    }

    /// <exception cref="AppException">
    /// <see cref="MembershipErrors.NotFound"/> when the membership is not an active one of this tenant, or
    /// <see cref="MembershipErrors.LastAdministrator"/> when the change would leave the tenant with none.
    /// </exception>
    public async Task<MembershipDetails> ChangeRoleAsync(ChangeRoleCommand command, ActorIdentity actor, AuditActor auditActor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(auditActor);
        await changeRoleValidator.ValidateAndThrowAsync(command, cancellationToken);
        var tenantId = TenantIdOrNotFound(command.TenantId);

        await using var change = await store.BeginChangeAsync(tenantId, cancellationToken);
        var membership = await FindOrThrowAsync(change, command.MembershipId, cancellationToken);
        var newRole = MembershipRoleNames.Parse(command.Role!);
        if (newRole == membership.Role)
        {
            // Nothing changes, so nothing is written and nothing is audited.
            return MembershipDetails.From(membership);
        }

        AdministratorRule.EnsureTenantKeepsAdministrator(membership, newRole, await change.CountActiveAdministratorsAsync(cancellationToken));
        var now = clock.GetUtcNow();
        var oldRole = membership.ChangeRole(newRole, now);
        change.Append(MembershipAuditRecord.RoleChanged(membership, oldRole, actor, now));
        change.Announce(OutgoingMessage.From(MembershipAuditEvents.RoleChanged(membership, auditActor, now)), now);
        await change.CommitAsync(cancellationToken);
        return MembershipDetails.From(membership);
    }

    /// <exception cref="AppException">
    /// <see cref="MembershipErrors.NotFound"/>, or <see cref="MembershipErrors.LastAdministrator"/> when the member is the
    /// tenant's only administrator.
    /// </exception>
    public async Task RemoveAsync(Guid tenantGuid, Guid membershipGuid, ActorIdentity actor, AuditActor auditActor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(auditActor);
        var tenantId = TenantIdOrNotFound(tenantGuid);

        await using var change = await store.BeginChangeAsync(tenantId, cancellationToken);
        var membership = await FindOrThrowAsync(change, membershipGuid, cancellationToken);
        AdministratorRule.EnsureTenantKeepsAdministrator(membership, roleAfter: null, await change.CountActiveAdministratorsAsync(cancellationToken));
        var now = clock.GetUtcNow();
        membership.Remove(now);
        change.Append(MembershipAuditRecord.Removed(membership, actor, now));
        change.Announce(OutgoingMessage.From(MembershipAuditEvents.Removed(membership, auditActor, now)), now);
        await change.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// The caller's own active memberships in tenants that are active right now (SPEC-0002 criterion 16). Issuer and
    /// subject are matched exactly, so the same subject from another issuer is a different person.
    /// </summary>
    public async Task<IReadOnlyList<MembershipSummary>> ListForIdentityAsync(ActorIdentity identity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var memberships = await store.ListActiveForIdentityAsync(identity.Issuer, identity.Subject, cancellationToken);
        if (memberships.Count == 0)
        {
            return [];
        }

        var availability = await tenants.GetAvailabilityAsync(
            memberships.Select(static membership => membership.TenantId.Value).Distinct().ToArray(),
            cancellationToken);
        return memberships
            .Where(membership => availability.TryGetValue(membership.TenantId.Value, out var state) && state == TenantAvailability.Active)
            .Select(static membership => new MembershipSummary(membership.Id.Value, membership.TenantId.Value, membership.Role.ToName()))
            .ToList();
    }

    // The check is a read, taken before the change. A tenant suspended in between still gets this one membership; the
    // next change is refused. Suspension never removes access by itself: the tenant routes check status (S6).
    private async Task EnsureTenantAcceptsChangesAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty)
        {
            throw new AppException(TenantContractErrors.NotFound);
        }

        var availability = await tenants.GetAvailabilityAsync([tenantId], cancellationToken);
        if (!availability.TryGetValue(tenantId, out var state))
        {
            throw new AppException(TenantContractErrors.NotFound);
        }

        if (state != TenantAvailability.Active)
        {
            throw new AppException(TenantContractErrors.Suspended);
        }
    }

    // A tenant id that cannot exist is a tenant that does not exist; it must not become a programming error.
    private static TenantId TenantIdOrNotFound(Guid tenantId) =>
        tenantId == Guid.Empty ? throw new AppException(MembershipErrors.NotFound) : new TenantId(tenantId);

    private static async Task<Membership> FindOrThrowAsync(IMembershipChange change, Guid membershipId, CancellationToken cancellationToken) =>
        membershipId == Guid.Empty
            ? throw new AppException(MembershipErrors.NotFound)
            : await change.FindActiveAsync(new MembershipId(membershipId), cancellationToken) ?? throw new AppException(MembershipErrors.NotFound);
}
