using Npgsql;
using NpgsqlTypes;

namespace TenantFlow.BuildingBlocks.Persistence.Retention;

/// <summary>
/// Runs retention rules for one tenant at a time (ADR-0025 decision 4). Every batch is its own transaction with the
/// tenant set for row-level security, as the application role: no role bypasses RLS and no statement spans two tenants.
/// A run that stops half-way leaves only whole batches done; the next run continues.
/// </summary>
public static class TenantRetention
{
    /// <summary>The most rows one statement changes in one transaction.</summary>
    public const int BatchSize = 500;

    /// <summary>Applies every rule to <paramref name="tenantId"/>'s rows as of <paramref name="now"/>; returns the rows changed.</summary>
    public static async Task<long> RunAsync(NpgsqlDataSource dataSource, Guid tenantId, IEnumerable<RetentionRule> rules, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(rules);
        long total = 0;
        foreach (var rule in rules)
        {
            long changed;
            do
            {
                changed = await RunBatchAsync(dataSource, tenantId, rule, now - rule.Keep, cancellationToken);
                total += changed;
            }
            while (changed >= BatchSize);
        }

        return total;
    }

    private static async Task<long> RunBatchAsync(NpgsqlDataSource dataSource, Guid tenantId, RetentionRule rule, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var scope = new NpgsqlCommand($"SELECT set_config('{TenantScopedTransaction.SettingName}', @tenant, true)", connection, transaction))
        {
            scope.Parameters.AddWithValue("tenant", tenantId.ToString("D"));
            await scope.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var command = new NpgsqlCommand(rule.Sql, connection, transaction);
        if (rule.Sql.Contains("@tenant", StringComparison.Ordinal))
        {
            command.Parameters.AddWithValue("tenant", NpgsqlDbType.Uuid, tenantId);
        }

        if (rule.Sql.Contains("@cutoff", StringComparison.Ordinal))
        {
            command.Parameters.AddWithValue("cutoff", NpgsqlDbType.TimestampTz, cutoff);
        }

        var changed = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        await transaction.CommitAsync(cancellationToken);
        return changed;
    }
}
