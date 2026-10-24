namespace TenantFlow.ImportsExports.Contracts;

/// <summary>
/// The read-only job view a support operator gets through an active support grant (SPEC-0009 criterion 17,
/// ADR-0027 decision 3): status, counts and failure code of imports and exports; never file content or row values. The
/// caller has checked the grant for the tenant and committed its access record before it asks.
/// </summary>
public interface ISupportJobView
{
    /// <summary>The newest <paramref name="limit"/> imports and exports of <paramref name="tenantId"/>, newest first.</summary>
    Task<IReadOnlyList<SupportJob>> ListAsync(Guid tenantId, int limit, CancellationToken cancellationToken);
}

/// <summary>An import or export as support sees it. <c>Kind</c> is <c>import</c> or <c>export</c>.</summary>
public sealed record SupportJob(
    Guid JobId,
    string Kind,
    string Status,
    int Created,
    int Updated,
    int Failed,
    int Rows,
    string? FailureCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);
