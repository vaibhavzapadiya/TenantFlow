using Microsoft.Extensions.Diagnostics.HealthChecks;
using TenantFlow.BuildingBlocks.Messaging.RabbitMq;

namespace TenantFlow.Worker.Health;

/// <summary>
/// Readiness: the broker connection is open, or can be opened within a short timeout (SPEC-0005 criterion 7).
/// One attempt and no retries inside a probe, as for PostgreSQL.
/// </summary>
internal sealed class RabbitMqReadinessCheck(RabbitMqConnection connection) : IHealthCheck
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        try
        {
            var open = await connection.GetAsync(timeout.Token);
            return open.IsOpen ? HealthCheckResult.Healthy() : new HealthCheckResult(context.Registration.FailureStatus, "RabbitMQ connection is closed");
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // A one-line reason for the health report and its log entry; never in the HTTP response.
            return new HealthCheckResult(
                context.Registration.FailureStatus,
                $"RabbitMQ is not reachable ({exception.GetType().Name})"); // The type only: third-party messages can name hosts and users (ADR-0031).
        }
    }
}
