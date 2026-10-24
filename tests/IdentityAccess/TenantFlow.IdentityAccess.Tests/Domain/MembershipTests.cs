using TenantFlow.BuildingBlocks.Abstractions.Errors;
using TenantFlow.BuildingBlocks.Abstractions.Identity;
using TenantFlow.IdentityAccess.Domain.Memberships;

namespace TenantFlow.IdentityAccess.Tests.Domain;

/// <summary>SPEC-0002 criteria 19 and 21 at the domain: what a membership is, and how it may change.</summary>
public sealed class MembershipTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = T0.AddMinutes(5);
    private static readonly ActorIdentity Member = new("https://idp.test/realms/tenantflow", "user-1");

    private static Membership NewMembership(MembershipRole role = MembershipRole.Customer) =>
        Membership.Create(new MembershipId(Guid.CreateVersion7()), new TenantId(Guid.CreateVersion7()), Member, role, T0);

    [Fact]
    public void A_new_membership_is_active_and_keeps_identity_role_and_time()
    {
        var tenant = new TenantId(Guid.CreateVersion7());
        var id = new MembershipId(Guid.CreateVersion7());

        var membership = Membership.Create(id, tenant, Member, MembershipRole.TenantAdmin, T0);

        Assert.Equal(id, membership.Id);
        Assert.Equal(tenant, membership.TenantId);
        Assert.Equal(Member.Issuer, membership.Issuer);
        Assert.Equal(Member.Subject, membership.Subject);
        Assert.Equal(MembershipRole.TenantAdmin, membership.Role);
        Assert.Equal(MembershipStatus.Active, membership.Status);
        Assert.True(membership.IsActive);
        Assert.Equal(T0, membership.CreatedAt);
        Assert.Equal(T0, membership.UpdatedAt);
    }

    [Fact]
    public void Ids_cannot_be_empty_and_a_default_id_is_refused_by_create()
    {
        Assert.Throws<ArgumentException>(() => new TenantId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new MembershipId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => Membership.Create(default, new TenantId(Guid.CreateVersion7()), Member, MembershipRole.Customer, T0));
        Assert.Throws<ArgumentException>(() => Membership.Create(new MembershipId(Guid.CreateVersion7()), default, Member, MembershipRole.Customer, T0));
    }

    [Fact]
    public void An_undefined_role_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Membership.Create(new MembershipId(Guid.CreateVersion7()), new TenantId(Guid.CreateVersion7()), Member, (MembershipRole)0, T0));
    }

    [Fact]
    public void Changing_the_role_returns_the_old_role_and_stamps_the_time()
    {
        var membership = NewMembership(MembershipRole.Customer);

        var old = membership.ChangeRole(MembershipRole.TenantAdmin, T1);

        Assert.Equal(MembershipRole.Customer, old);
        Assert.Equal(MembershipRole.TenantAdmin, membership.Role);
        Assert.Equal(T1, membership.UpdatedAt);
        Assert.Equal(T0, membership.CreatedAt);
    }

    [Fact]
    public void Removing_ends_the_membership_and_it_cannot_change_again()
    {
        var membership = NewMembership();

        membership.Remove(T1);

        Assert.Equal(MembershipStatus.Removed, membership.Status);
        Assert.False(membership.IsActive);
        Assert.Equal(T1, membership.UpdatedAt);
        Assert.Equal(MembershipErrors.NotFound.Code, Assert.Throws<AppException>(() => membership.Remove(T1)).Error.Code);
        Assert.Equal(MembershipErrors.NotFound.Code, Assert.Throws<AppException>(() => membership.ChangeRole(MembershipRole.TenantAdmin, T1)).Error.Code);
        Assert.Equal(MembershipRole.Customer, membership.Role);
    }

    [Theory]
    [InlineData("user-1", null)]
    [InlineData("a", null)]
    [InlineData("name with spaces and unicode é", null)]
    [InlineData(null, "membership.subject.required")]
    [InlineData("", "membership.subject.required")]
    [InlineData("   ", "membership.subject.required")]
    [InlineData("line\nbreak", "membership.subject.invalid_characters")]
    [InlineData("tab\there", "membership.subject.invalid_characters")]
    [InlineData("nul\0byte", "membership.subject.invalid_characters")]
    public void The_subject_rule_has_one_definition(string? subject, string? expectedCode)
    {
        Assert.Equal(expectedCode, Membership.CheckSubject(subject)?.Code);
    }

    [Fact]
    public void A_subject_longer_than_the_stored_column_is_too_long()
    {
        Assert.Null(Membership.CheckSubject(new string('s', IdentityLimits.SubjectMaxLength)));
        Assert.Equal("membership.subject.too_long", Membership.CheckSubject(new string('s', IdentityLimits.SubjectMaxLength + 1))?.Code);
    }
}
