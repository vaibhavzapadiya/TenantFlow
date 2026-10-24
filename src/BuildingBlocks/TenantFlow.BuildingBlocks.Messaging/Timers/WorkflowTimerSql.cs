using System.Text.RegularExpressions;

namespace TenantFlow.BuildingBlocks.Messaging.Timers;

/// <summary>
/// The statement a module store runs, inside its state change's transaction, to schedule a timer (ADR-0022 decision 7).
/// Scheduling a kind and subject again moves the one timer and reopens it, only within the same tenant: a schedule for
/// another tenant's row changes nothing, so no tenant can re-arm a timer that runs in another.
/// </summary>
public static partial class WorkflowTimerSql
{
    /// <summary>
    /// <c>INSERT ... ON CONFLICT</c> for <paramref name="schema"/>, with positional parameters
    /// {0} id, {1} tenant id, {2} kind, {3} subject id, {4} due at; for <c>ExecuteSqlRawAsync</c>.
    /// </summary>
    public static string Schedule(string schema)
    {
        if (schema is null || !SchemaName().IsMatch(schema))
        {
            throw new ArgumentException("The timer schema must be a lower-case identifier.", nameof(schema));
        }

        return $$"""
            INSERT INTO {{schema}}.{{WorkflowTimerConfiguration.Table}} AS timer (id, tenant_id, kind, subject_id, due_at, attempts)
            VALUES ({0}, {1}, {2}, {3}, {4}, 0)
            ON CONFLICT (kind, subject_id) DO UPDATE
            SET due_at = EXCLUDED.due_at, attempts = 0, locked_until = NULL, completed_at = NULL, failures = 0, dead_at = NULL
            WHERE timer.tenant_id = EXCLUDED.tenant_id
            """;
    }

    [GeneratedRegex("^[a-z][a-z_]{0,62}$")]
    private static partial Regex SchemaName();
}
