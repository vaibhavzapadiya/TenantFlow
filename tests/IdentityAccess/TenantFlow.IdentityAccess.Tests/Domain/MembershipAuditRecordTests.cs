using TenantFlow.BuildingBlocks.Abstractions.Identity;
using TenantFlow.IdentityAccess.Domain.Auditing;
using TenantFlow.IdentityAccess.Domain.Memberships;

namespace TenantFlow.IdentityAccess.Tests.Domain;

/// <summary>SPEC-0002 criterion 21: an audit record can only describe the change it was built from.</summary>
public sealed class MembershipAuditRecordTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly ActorIdentity Actor = new("https://idp.test/realms/tenantflow", "admin-1");

    private static Membership NewMembership(MembershipRole role) =>
        Membership.Create(
            new MembershipId(Guid.CreateVersion7()),
            new TenantId(Guid.CreateVersion7()),
            new ActorIdentity("https://idp.test/realms/tenantflow", "user-2"),
            role,
            At.AddDays(-1));

    [Fact]
    public void Added_records_the_new_role_and_no_old_role()
    {
        var membership = NewMembership(MembershipRole.Customer);

        var record = MembershipAuditRecord.Added(membership, Actor, At);

        Assert.Equal("membership.added", record.Action);
        Assert.Equal(membership.TenantId, record.TenantId);
        Assert.Equal(membership.Id, record.MembershipId);
        Assert.Equal(Actor.Issuer, record.ActorIssuer);
        Assert.Equal(Actor.Subject, record.ActorSubject);
        Assert.Null(record.OldRole);
        Assert.Equal(MembershipRole.Customer, record.NewRole);
        Assert.Equal(At, record.OccurredAt);
    }

    [Fact]
    public void RoleChanged_records_both_roles()
    {
        var membership = NewMembership(MembershipRole.Customer);
        var old = membership.ChangeRole(MembershipRole.TenantAdmin, At);

        var record = MembershipAuditRecord.RoleChanged(membership, old, Actor, At);

        Assert.Equal("membership.role_changed", record.Action);
        Assert.Equal(MembershipRole.Customer, record.OldRole);
        Assert.Equal(MembershipRole.TenantAdmin, record.NewRole);
    }

    [Fact]
    public void Removed_records_the_role_held_and_no_new_role()
    {
        var membership = NewMembership(MembershipRole.TenantAdmin);
        membership.Remove(At);

        var record = MembershipAuditRecord.Removed(membership, Actor, At);

        Assert.Equal("membership.removed", record.Action);
        Assert.Equal(MembershipRole.TenantAdmin, record.OldRole);
        Assert.Null(record.NewRole);
    }

    [Fact]
    public void A_role_change_that_changes_nothing_is_not_an_audit_event()
    {
        var membership = NewMembership(MembershipRole.Customer);

        Assert.Throws<ArgumentException>(() => MembershipAuditRecord.RoleChanged(membership, MembershipRole.Customer, Actor, At));
    }

    [Fact]
    public void Factories_refuse_missing_inputs()
    {
        var membership = NewMembership(MembershipRole.Customer);

        Assert.Throws<ArgumentNullException>(() => MembershipAuditRecord.Added(null!, Actor, At));
        Assert.Throws<ArgumentNullException>(() => MembershipAuditRecord.Added(membership, null!, At));
        Assert.Throws<ArgumentNullException>(() => MembershipAuditRecord.RoleChanged(membership, MembershipRole.TenantAdmin, null!, At));
        Assert.Throws<ArgumentNullException>(() => MembershipAuditRecord.Removed(membership, null!, At));
    }
}
