using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TenantFlow.Worker.Heartbeat;

namespace TenantFlow.Worker.Tests;

/// <summary>
/// SPEC-0001 criteria 11, 12, and 13 for the heartbeat loop. Time is simulated with
/// <see cref="FakeTimeProvider"/>, so no test waits for real intervals.
/// </summary>
public sealed class PlatformHeartbeatServiceTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Records_exactly_one_heartbeat_per_elapsed_interval()
    {
        var time = new FakeTimeProvider();
        using var recorder = new CountingRecorder();
        using var service = CreateService(time, recorder, new FakeLogger<PlatformHeartbeatService>());
        await service.StartAsync(TestContext.Current.CancellationToken);

        time.Advance(Interval - TimeSpan.FromSeconds(1));
        time.Advance(TimeSpan.FromSeconds(1));
        await recorder.WaitForCountAsync(1);
        Assert.Equal(1, recorder.Count);

        time.Advance(Interval);
        await recorder.WaitForCountAsync(2);
        Assert.Equal(2, recorder.Count);

        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Stops_within_five_seconds_without_an_unhandled_exception()
    {
        var time = new FakeTimeProvider();
        using var recorder = new CountingRecorder();
        using var service = CreateService(time, recorder, new FakeLogger<PlatformHeartbeatService>());
        await service.StartAsync(TestContext.Current.CancellationToken);

        var stopwatch = Stopwatch.StartNew();
        using var stopDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await service.StopAsync(stopDeadline.Token);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.NotNull(service.ExecuteTask);
        // .NET 10 starts ExecuteAsync only after StartAsync returns (SPEC-0015), so stopping at once can cancel the
        // loop before its body runs. Finished-or-cancelled is a clean shutdown either way; a faulted task is the
        // unhandled exception this criterion guards against.
        Assert.True(service.ExecuteTask.IsCompleted, "The execute task did not finish.");
        Assert.False(service.ExecuteTask.IsFaulted, $"The execute task faulted: {service.ExecuteTask.Exception}");
    }

    [Fact]
    public async Task A_failing_tick_is_logged_and_the_next_tick_still_runs()
    {
        var time = new FakeTimeProvider();
        using var recorder = new CountingRecorder(throwOnCall: 1);
        var logger = new FakeLogger<PlatformHeartbeatService>();
        using var service = CreateService(time, recorder, logger);
        await service.StartAsync(TestContext.Current.CancellationToken);

        time.Advance(Interval);
        await recorder.WaitForCountAsync(1);
        time.Advance(Interval);
        await recorder.WaitForCountAsync(2);

        var error = Assert.Single(logger.Collector.GetSnapshot(), record => record.Level == LogLevel.Error);
        Assert.IsType<InvalidOperationException>(error.Exception);

        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    private static PlatformHeartbeatService CreateService(
        TimeProvider time, IHeartbeatRecorder recorder, ILogger<PlatformHeartbeatService> logger) =>
        new(Options.Create(new HeartbeatOptions { HeartbeatInterval = Interval }), recorder, time, logger);

    /// <summary>Counts calls, signals each one, and can throw on a chosen call.</summary>
    private sealed class CountingRecorder(int? throwOnCall = null) : IHeartbeatRecorder, IDisposable
    {
        private readonly SemaphoreSlim _calls = new(0);
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Record(long sequence)
        {
            var call = Interlocked.Increment(ref _count);
            _calls.Release();
            if (call == throwOnCall)
            {
                throw new InvalidOperationException("Simulated heartbeat failure.");
            }
        }

        public void Dispose() => _calls.Dispose();

        public async Task WaitForCountAsync(int expected)
        {
            while (Count < expected)
            {
                if (!await _calls.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken))
                {
                    throw new TimeoutException($"Expected {expected} heartbeat(s), saw {Count}.");
                }
            }
        }
    }
}
