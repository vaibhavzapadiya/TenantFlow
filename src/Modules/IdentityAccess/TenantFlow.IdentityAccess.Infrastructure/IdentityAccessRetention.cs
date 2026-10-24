using TenantFlow.BuildingBlocks.Persistence.Retention;

namespace TenantFlow.IdentityAccess.Infrastructure;

/// <summary>
/// The IdentityAccess module's retention rules (SPEC-0009 criteria 8, 9 and 10, ADR-0025 decision 3). The Worker's retention task runs them
/// tenant by tenant; see <c>docs/security/privacy-and-retention.md</c>.
/// </summary>
public static class IdentityAccessRetention
{
    /// <summary>The issuer a pseudonymised membership is given; no identity provider uses it.</summary>
    public const string PseudonymIssuer = "erased";

    /// <summary>A removed membership keeps its issuer and subject this long, then they are replaced (criterion 9).</summary>
    public static readonly TimeSpan RemovedMemberships = TimeSpan.FromDays(30);

    /// <summary>Audit records are kept this long; the purge function in the database holds the same cutoff.</summary>
    public static readonly TimeSpan AuditRecords = TimeSpan.FromDays(400);

    public static IReadOnlyList<RetentionRule> Rules { get; } =
    [
        // The row stays: orders, payments and audit records reference its id. The person can be added again, because
        // the unique identity index covers active memberships only.
        new(
            "identity_access.removed_memberships",
            RemovedMemberships,
            $"""
            WITH batch AS (
                SELECT ctid FROM identity_access.memberships
                WHERE tenant_id = @tenant AND status = 'removed' AND updated_at < @cutoff AND issuer <> '{PseudonymIssuer}'
                LIMIT {TenantRetention.BatchSize}),
                 changed AS (
                UPDATE identity_access.memberships SET issuer = '{PseudonymIssuer}', subject = '{PseudonymIssuer}:' || id::text
                WHERE ctid IN (SELECT ctid FROM batch) RETURNING 1)
            SELECT count(*) FROM changed
            """),
        new("identity_access.membership_audit", AuditRecords, "SELECT identity_access.purge_expired_audit()"),
    ];
}
