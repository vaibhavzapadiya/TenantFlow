using Npgsql;
using NpgsqlTypes;

namespace TenantFlow.BuildingBlocks.Messaging.Outbox;

/// <summary>
/// The publisher's access to one module's outbox table (ADR-0021). Every method is a single statement in its own
/// implicit transaction: nothing here holds a transaction open while the broker is called (AGENTS.md rule 3).
/// Rows are claimed with a lease, so concurrent publishers split the work and a crashed publisher's rows come back.
/// </summary>
public sealed partial class OutboxStore
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly string _claimSql;
    private readonly string _markSql;
    private readonly string _deferSql;
    private readonly string _deleteSql;
    private readonly string _backlogSql;

    /// <param name="dataSource">The application role's connection (the outbox is not under row-level security).</param>
    /// <param name="schema">The owning module's schema, for example <c>orders</c>; also the module label in metrics.</param>
    public OutboxStore(NpgsqlDataSource dataSource, string schema)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        SqlSchema.Ensure(schema, nameof(schema), "outbox");

        _dataSource = dataSource;
        Module = schema;
        var table = $"{schema}.{OutboxMessageConfiguration.Table}";
        _claimSql = $"""
            UPDATE {table} SET locked_until = @until, attempts = attempts + 1
            WHERE id IN (
                SELECT id FROM {table}
                WHERE published_at IS NULL AND (locked_until IS NULL OR locked_until <= @now)
                ORDER BY occurred_at, id
                LIMIT @batch
                FOR UPDATE SKIP LOCKED)
            RETURNING id, tenant_id, type, version, payload::text, correlation_id, traceparent, occurred_at
            """;
        _markSql = $"UPDATE {table} SET published_at = @now, locked_until = NULL, last_error = NULL WHERE id = ANY(@ids)";
        // Exponential backoff on the attempts made so far (2 s, 4 s, 8 s ... at most 5 minutes), so a row the broker keeps
        // refusing waits longer each time instead of heading every batch.
        _deferSql = $"""
            UPDATE {table}
            SET locked_until = @now + least(interval '1 second' * power(2, attempts), interval '5 minutes'), last_error = @error
            WHERE id = @id AND published_at IS NULL
            """;
        _deleteSql = $"""
            DELETE FROM {table} WHERE id IN (
                SELECT id FROM {table} WHERE published_at < @cutoff ORDER BY published_at LIMIT @limit)
            """;
        _backlogSql = $"SELECT count(*), min(occurred_at) FROM {table} WHERE published_at IS NULL";
    }

    public string Module { get; }

    /// <summary>
    /// Claims up to <paramref name="batchSize"/> pending rows, oldest first, that no other publisher holds, until
    /// <paramref name="now"/> plus <paramref name="lease"/>.
    /// </summary>
    public async Task<IReadOnlyList<PendingMessage>> ClaimAsync(int batchSize, DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(_claimSql);
        command.Parameters.AddWithValue("until", now + lease);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("batch", batchSize);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var claimed = new List<PendingMessage>();
        while (await reader.ReadAsync(cancellationToken))
        {
            claimed.Add(new PendingMessage(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetFieldValue<DateTimeOffset>(7)));
        }

        // RETURNING does not keep the subquery's order.
        return [.. claimed.OrderBy(static m => m.OccurredAt).ThenBy(static m => m.Id)];
    }

    /// <summary>Marks confirmed messages published; they are never claimed again.</summary>
    public async Task MarkPublishedAsync(IReadOnlyCollection<Guid> ids, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(_markSql);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.Add(new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = ids.ToArray() });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Holds back a row the broker did not confirm, with the reason, for a backoff that grows with its attempts. Other rows
    /// are claimed meanwhile, so one message the broker keeps refusing never holds back the rest.
    /// </summary>
    public async Task DeferAsync(Guid id, string error, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(_deferSql);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("error", error.Length <= 1024 ? error : error[..1024]);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Deletes up to <paramref name="limit"/> rows published before <paramref name="cutoff"/>. Pending rows are never deleted.</summary>
    public async Task<int> DeletePublishedBeforeAsync(DateTimeOffset cutoff, int limit, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(_deleteSql);
        command.Parameters.AddWithValue("cutoff", cutoff);
        command.Parameters.AddWithValue("limit", limit);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>How many rows wait to be published, and when the oldest of them occurred.</summary>
    public async Task<(long Pending, DateTimeOffset? Oldest)> GetBacklogAsync(CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand(_backlogSql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return (reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1));
    }
}
