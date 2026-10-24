using TenantFlow.BuildingBlocks.Abstractions.Identity;
using TenantFlow.Tenants.Domain.Auditing;
using TenantFlow.Tenants.Domain.Tenants;

namespace TenantFlow.Tenants.Tests.Domain;

/// <summary>
/// The audit trail (blueprint section 13) is built by the domain, so an action can never contradict the status
/// change it describes (pre-S5 review 2.10). The stored action names are a contract: they never change.
/// </summary>
public sealed class TenantAuditRecordTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly ActorIdentity Actor = new("https://idp.example/realms/platform", "operator-a");

    [Fact]
    public void Created_records_the_new_tenant_with_no_previous_status()
    {
        var tenant = Tenant.Create(Guid.NewGuid(), "Acme", At);

        var record = TenantAuditRecord.Created(tenant, Actor, At);

        Assert.Equal(tenant.Id, record.TenantId);
        Assert.Equal("tenant.created", record.Action);
        Assert.Null(record.OldStatus);
        Assert.Equal(TenantStatus.Active, record.NewStatus);
        AssertActorAndTime(record);
    }

    [Fact]
    public void Suspended_records_the_only_transition_that_suspends()
    {
        var id = Guid.NewGuid();

        var record = TenantAuditRecord.Suspended(id, Actor, At);

        Assert.Equal(id, record.TenantId);
        Assert.Equal("tenant.suspended", record.Action);
        Assert.Equal(TenantStatus.Active, record.OldStatus);
        Assert.Equal(TenantStatus.Suspended, record.NewStatus);
        AssertActorAndTime(record);
    }

    [Fact]
    public void Reactivated_records_the_only_transition_that_reactivates()
    {
        var id = Guid.NewGuid();

        var record = TenantAuditRecord.Reactivated(id, Actor, At);

        Assert.Equal(id, record.TenantId);
        Assert.Equal("tenant.reactivated", record.Action);
        Assert.Equal(TenantStatus.Suspended, record.OldStatus);
        Assert.Equal(TenantStatus.Active, record.NewStatus);
        AssertActorAndTime(record);
    }

    [Fact]
    public void Action_names_are_a_stable_contract()
    {
        // They are stored as text and read by audit queries; renaming one orphans every existing record.
        Assert.Equal("tenant.created", TenantAuditActions.Created);
        Assert.Equal("tenant.suspended", TenantAuditActions.Suspended);
        Assert.Equal("tenant.reactivated", TenantAuditActions.Reactivated);
    }

    [Fact]
    public void A_record_cannot_be_made_without_an_actor()
    {
        Assert.Throws<ArgumentNullException>(() => TenantAuditRecord.Suspended(Guid.NewGuid(), null!, At));
        Assert.Throws<ArgumentNullException>(() => TenantAuditRecord.Reactivated(Guid.NewGuid(), null!, At));
        Assert.Throws<ArgumentNullException>(() => TenantAuditRecord.Created(Tenant.Create(Guid.NewGuid(), "Acme", At), null!, At));
    }

    private static void AssertActorAndTime(TenantAuditRecord record)
    {
        Assert.Equal(Actor.Issuer, record.ActorIssuer);
        Assert.Equal(Actor.Subject, record.ActorSubject);
        Assert.Equal(At, record.OccurredAt);
    }
}
