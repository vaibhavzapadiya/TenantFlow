using Npgsql;

namespace TenantFlow.BuildingBlocks.Messaging.Timers;

/// <summary>
/// The runner's access to one module's timer table (ADR-0022 decision 7). Every method is a single statement in its own
/// implicit transaction: nothing here holds a transaction open while a handler calls a provider (AGENTS.md rule 3). Due
/// timers are claimed with a lease, so concurrent workers split them and a crashed worker's timers come back.
/// </summary>
public sealed partial class WorkflowTimerStore
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly string _claimSql;
    private readonly string _completeSql;
    private readonly string _rescheduleSql;
    private readonly string _failSql;
    private readonly string _dueSql;
    private readonly string _deadSql;
    private readonly string _unresolvedSql;

    /// <param name="dataSource">The application role's connection (timers are not under row-level security).</param>
    /// <param name="schema">The owning module's schema, for example <c>payments</c>.</param>
    public WorkflowTimerStore(NpgsqlDataSource dataSource, string schema)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        SqlSchema.Ensure(schema, nameof(schema), "timer");

        _dataSource = dataSource;
        Module = schema;
        var table = $"{schema}.{WorkflowTimerConfiguration.Table}";
        _claimSql = $"""
            UPDATE {table} SET locked_until = @until
            WHERE id IN (
                SELECT id FROM {table}
                WHERE completed_at IS NULL AND dead_at IS NULL AND due_at <= @now AND (locked_until IS NULL OR locked_until <= @now)
                ORDER BY due_at, id
                LIMIT @batch
                FOR UPDATE SKIP LOCKED)
            RETURNING id, tenant_id, kind, subject_id, attempts, failures
            """;
        // Only the claim that is still current may finish the timer: a lease that expired and was claimed again belongs
        // to the new claimer.
        _completeSql = $"UPDATE {table} SET completed_at = @now, locked_until = NULL, attempts = attempts + 1, failures = 0 WHERE id = @id AND locked_until = @until";
        _rescheduleSql = $"UPDATE {table} SET due_at = @due, locked_until = NULL, attempts = attempts + 1, failures = 0 WHERE id = @id AND locked_until = @until";
        // A failed run counts in a row; the one that reaches the limit marks the row dead (ADR-0023 decision 1).
        _failSql = $"""
            UPDATE {table} SET due_at = @due, locked_until = NULL, attempts = attempts + 1, failures = failures + 1,
                dead_at = CASE WHEN failures + 1 >= @max THEN @now END
            WHERE id = @id AND locked_until = @until
            RETURNING dead_at IS NOT NULL
            """;
        // Kinds and times only: never a tenant or subject id, so metrics can read it without tenant data.
        _dueSql = $"""
            SELECT kind, count(*), min(due_at) FROM {table}
            WHERE completed_at IS NULL AND dead_at IS NULL AND due_at <= @now
            GROUP BY kind
            """;
        _deadSql = $"SELECT count(*) FROM {table} WHERE dead_at IS NOT NULL";
        // Open timers that already ran once and were rescheduled: their subject is still unresolved. Subject ids are
        // version 7 UUIDs, whose first 48 bits are the creation time in Unix milliseconds (PostgreSQL 17's
        // uuid_extract_timestamp reads version 1 only); a kind with no version 7 subject falls back to its oldest due time.
        _unresolvedSql = $"""
            SELECT kind, count(*), coalesce(
                min(CASE WHEN substr(subject_id::text, 15, 1) = '7'
                    THEN to_timestamp(('x' || substr(replace(subject_id::text, '-', ''), 1, 12))::bit(48)::bigint / 1000.0) END),
                min(due_at))
            FROM {table}
            WHERE completed_at IS NULL AND dead_at IS NULL AND attempts > 0
            GROUP BY kind
            """;
    }

    public string Module { get; }

    /// <summary>Claims up to <paramref name="batchSize"/> due timers that no other worker holds, for <paramref name="lease"/>.</summary>
    public async Task<IReadOnlyList<DueTimer>> ClaimAsync(int batchSize, DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken)
    {
        // PostgreSQL keeps microseconds: the lease is cut to them, so the value handed back compares equal to the stored one.
        var until = now + lease;
        until = until.AddTicks(-(until.Ticks % 10));
        await using var command = _dataSource.CreateCommand(_claimSql);
        command.Parameters.AddWithValue("until", until);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("batch", batchSize);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var claimed = new List<DueTimer>();
        while (await reader.ReadAsync(cancellationToken))
        {
            claimed.Add(new DueTimer(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetGuid(3), reader.GetInt32(4), until, reader.GetInt32(5)));
        }

        return claimed;
    }

    /// <summary>Finishes a timer this claim still holds. False when the claim was lost (its lease passed and another took it).</summary>
    public async Task<bool> CompleteAsync(DueTimer timer, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timer);
        await using var command = _dataSource.CreateCommand(_completeSql);
        command.Parameters.AddWithValue("id", timer.Id);
        command.Parameters.AddWithValue("until", timer.LockedUntil);
        command.Parameters.AddWithValue("now", now);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>Moves a timer this claim still holds to <paramref name="dueAt"/>. False when the claim was lost.</summary>
    public async Task<bool> RescheduleAsync(DueTimer timer, DateTimeOffset dueAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timer);
        await using var command = _dataSource.CreateCommand(_rescheduleSql);
        command.Parameters.AddWithValue("id", timer.Id);
        command.Parameters.AddWithValue("until", timer.LockedUntil);
        command.Parameters.AddWithValue("due", dueAt);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>
    /// Records a failed run of a timer this claim still holds and moves it to <paramref name="dueAt"/>; the
    /// <paramref name="maxFailures"/>-th failure in a row marks it dead instead. Null when the claim was lost, otherwise
    /// whether it is now dead.
    /// </summary>
    public async Task<bool?> FailAsync(DueTimer timer, DateTimeOffset dueAt, DateTimeOffset now, int maxFailures, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timer);
        await using var command = _dataSource.CreateCommand(_failSql);
        command.Parameters.AddWithValue("id", timer.Id);
        command.Parameters.AddWithValue("until", timer.LockedUntil);
        command.Parameters.AddWithValue("due", dueAt);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("max", maxFailures);
        return await command.ExecuteScalarAsync(cancellationToken) is bool dead ? dead : null;
    }

    /// <summary>Per kind, the timers due at <paramref name="now"/> (claimed or not) and the oldest due time: for metrics.</summary>
    public async Task<IReadOnlyList<DueKind>> DueByKindAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(_dueSql);
        command.Parameters.AddWithValue("now", now);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var due = new List<DueKind>();
        while (await reader.ReadAsync(cancellationToken))
        {
            due.Add(new DueKind(reader.GetString(0), reader.GetInt64(1), reader.GetFieldValue<DateTimeOffset>(2)));
        }

        return due;
    }

    /// <summary>How many timers of the module are dead (SPEC-0010 criterion 3): a count only, for metrics.</summary>
    public async Task<long> CountDeadAsync(CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(_deadSql);
        return await command.ExecuteScalarAsync(cancellationToken) is long count ? count : 0;
    }

    /// <summary>
    /// Per kind, the open timers that already ran and were rescheduled (their subject is unresolved), and the creation time
    /// of the oldest subject, for metrics (SPEC-0010 criterion 4). Kinds and times only, never an id.
    /// </summary>
    public async Task<IReadOnlyList<DueKind>> UnresolvedByKindAsync(CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(_unresolvedSql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var unresolved = new List<DueKind>();
        while (await reader.ReadAsync(cancellationToken))
        {
            unresolved.Add(new DueKind(reader.GetString(0), reader.GetInt64(1), reader.GetFieldValue<DateTimeOffset>(2)));
        }

        return unresolved;
    }
}
