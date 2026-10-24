using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace TenantFlow.BuildingBlocks.Observability;

/// <summary>
/// Readiness for the API and the Worker: PostgreSQL accepts a connection and answers <c>SELECT 1</c>.
/// One attempt with a short timeout and no retries. Probes run often, and retrying inside a probe
/// would multiply load on a database that is already struggling (blueprint section 14).
/// </summary>
internal sealed class PostgreSqlReadinessCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        try
        {
            await using var command = dataSource.CreateCommand("SELECT 1");
            await command.ExecuteScalarAsync(timeout.Token);
            return HealthCheckResult.Healthy();
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The health check service logs every failed probe. A one-line reason (no stack trace)
            // keeps an outage from flooding the logs while still saying why. It goes into the health
            // report and its log entry, never into the HTTP response.
            return new HealthCheckResult(
                context.Registration.FailureStatus,
                $"PostgreSQL is not reachable ({exception.GetType().Name})"); // The type only: third-party messages can name hosts and users (ADR-0031).
        }
    }
}
