using Microsoft.Extensions.Options;

namespace TenantFlow.Worker.Heartbeat;

/// <summary>
/// Records a heartbeat every <see cref="HeartbeatOptions.HeartbeatInterval"/>. Its purpose is
/// operational: a worker that stops emitting heartbeats is stuck or gone (alerting in M10).
/// The interval runs on <see cref="TimeProvider"/>, so tests control time instead of sleeping.
/// </summary>
internal sealed partial class PlatformHeartbeatService(
    IOptions<HeartbeatOptions> options,
    IHeartbeatRecorder recorder,
    TimeProvider timeProvider,
    ILogger<PlatformHeartbeatService> logger) : BackgroundService
{
    // Created with the service, not inside ExecuteAsync: .NET 10 no longer runs the synchronous part of
    // ExecuteAsync before StartAsync returns (SPEC-0015), and the interval must be on the TimeProvider by then.
    private readonly PeriodicTimer _timer = new(options.Value.HeartbeatInterval, timeProvider);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        long sequence = 0;

        try
        {
            while (await _timer.WaitForNextTickAsync(stoppingToken))
            {
                sequence++;
                try
                {
                    recorder.Record(sequence);
                }
                catch (Exception exception)
                {
                    // One failed tick must not stop the worker: log it and let the next tick run.
                    LogHeartbeatFailed(logger, exception, sequence);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    public override void Dispose()
    {
        _timer.Dispose();
        base.Dispose();
    }

    [LoggerMessage(EventId = 1002, Level = LogLevel.Error, Message = "Worker heartbeat {Sequence} failed")]
    private static partial void LogHeartbeatFailed(ILogger logger, Exception exception, long sequence);
}
