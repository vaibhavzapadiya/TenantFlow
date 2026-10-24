namespace TenantFlow.BuildingBlocks.Persistence.Retention;

/// <summary>
/// One retention statement of a module (ADR-0025 decision 4). <see cref="Sql"/> changes at most
/// <see cref="TenantRetention.BatchSize"/> rows of one tenant and returns how many as a single number. It may use
/// <c>@tenant</c> (the tenant being purged, also set for row-level security) and <c>@cutoff</c> (now minus
/// <see cref="Keep"/>); the runner repeats it while a batch is full.
/// </summary>
/// <param name="Name">A stable name such as <c>orders.processed_messages</c>.</param>
/// <param name="Keep">How long a row is kept after the time the statement compares with <c>@cutoff</c>.</param>
/// <param name="Sql">The statement.</param>
public sealed record RetentionRule(string Name, TimeSpan Keep, string Sql)
{
    /// <summary>
    /// Deletes the tenant's rows of <paramref name="table"/> that match <paramref name="expired"/>, a condition on
    /// <c>@cutoff</c>, a batch at a time.
    /// </summary>
    public static RetentionRule Delete(string name, string table, string expired, TimeSpan keep) => new(
        name,
        keep,
        $"""
        WITH batch AS (SELECT ctid FROM {table} WHERE tenant_id = @tenant AND ({expired}) LIMIT {TenantRetention.BatchSize}),
             deleted AS (DELETE FROM {table} WHERE ctid IN (SELECT ctid FROM batch) RETURNING 1)
        SELECT count(*) FROM deleted
        """);
}
