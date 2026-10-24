using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TenantFlow.BuildingBlocks.Abstractions.Workflow;

namespace TenantFlow.BuildingBlocks.Messaging.Timers;

/// <summary>
/// Runs due workflow timers (ADR-0022 decision 7, ADR-0023 decision 1): claims a batch of one module's timers with a lease,
/// runs each in a DI scope of its own that acts for the timer's tenant only, then completes or reschedules it. A handler
/// that throws is retried with a growing delay, so one failing subject never stops the rest, and after
/// <see cref="MaxFailures"/> failures in a row the timer is dead: logged and never claimed again until an operator clears
/// it. An unknown kind is held back an hour and logged. Timers run one after another, so a timer whose lease is about to
/// pass while it waits in the batch is skipped: another worker may already have claimed it, and it comes back to whoever
/// claims it next (M6 review fix). The Worker's scheduler calls <see cref="RunDueAsync(WorkflowTimerStore, CancellationToken)"/>
/// once per module.
/// </summary>
public sealed partial class WorkflowTimerRunner(
    IEnumerable<WorkflowTimerStore> stores,
    IServiceScopeFactory scopes,
    WorkflowTimerHandlers handlers,
    TimeProvider clock,
    ILogger<WorkflowTimerRunner> logger)
{
    /// <summary>Small enough that a batch of slow provider calls (10 s each at most) fits well inside one lease.</summary>
    public const int BatchSize = 10;

    /// <summary>Handler failures in a row after which a timer is dead (ADR-0023 decision 1, SPEC-0007 criterion 8).</summary>
    public const int MaxFailures = 20;

    /// <summary>Long enough for a provider call; a crashed worker's timers come back after it.</summary>
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan UnknownKindDelay = TimeSpan.FromHours(1);
    // Doubling up to an hour: the 20 failures before a row is dead then span about 15 hours, so an outage of a dependency
    // for an evening does not kill renewals and reconciliations (review fix R12).
    private static readonly TimeSpan MaxFailureDelay = TimeSpan.FromHours(1);

    /// <summary>A timer is started only while at least this much of its lease is left.</summary>
    private static readonly TimeSpan LeaseMargin = TimeSpan.FromSeconds(30);

    private readonly IReadOnlyList<WorkflowTimerStore> _stores = [.. stores];

    /// <summary>Every module's timer store, by schema.</summary>
    public IReadOnlyList<WorkflowTimerStore> Stores => _stores;

    /// <summary>Runs one batch of due timers per module. Returns how many were run.</summary>
    public async Task<int> RunDueAsync(CancellationToken cancellationToken)
    {
        var run = 0;
        foreach (var store in _stores)
        {
            run += await RunDueAsync(store, cancellationToken);
        }

        return run;
    }

    /// <summary>Runs one batch of one module's due timers. Returns how many were run.</summary>
    public async Task<int> RunDueAsync(WorkflowTimerStore store, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        var run = 0;
        foreach (var timer in await store.ClaimAsync(BatchSize, clock.GetUtcNow(), Lease, cancellationToken))
        {
            if (clock.GetUtcNow() >= timer.LockedUntil - LeaseMargin)
            {
                LogLeaseExpiring(logger, timer.Id, timer.Kind, store.Module);
                continue;
            }

            var finished = await RunAsync(store, timer, cancellationToken);
            if (!finished)
            {
                LogLeaseLost(logger, timer.Id, timer.Kind, store.Module);
            }

            run++;
        }

        return run;
    }

    // Runs the timer and stores what became of it. False when the claim was lost meanwhile.
    private async Task<bool> RunAsync(WorkflowTimerStore store, DueTimer timer, CancellationToken cancellationToken)
    {
        if (handlers.Find(timer.Kind) is not { } handler)
        {
            LogUnknownKind(logger, timer.Id, timer.Kind, store.Module);
            return await store.RescheduleAsync(timer, clock.GetUtcNow() + UnknownKindDelay, cancellationToken);
        }

        TimerDecision decision;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            handlers.EnterTenant(scope.ServiceProvider, timer.TenantId);
            decision = await handler(scope.ServiceProvider, timer, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // 1, 2, 4 ... minutes, at most 60: a dependency outage delays the work without losing it.
            var now = clock.GetUtcNow();
            var delay = TimeSpan.FromMinutes(Math.Min(Math.Pow(2, Math.Min(timer.Failures, 10)), MaxFailureDelay.TotalMinutes));
            LogHandlerFailed(logger, exception, timer.Id, timer.Kind, store.Module);
            var dead = await store.FailAsync(timer, now + delay, now, MaxFailures, cancellationToken);
            if (dead == true)
            {
                LogDead(logger, timer.Id, timer.Kind, store.Module, MaxFailures);
            }

            return dead is not null;
        }

        return decision.RunAgainAt is { } dueAt
            ? await store.RescheduleAsync(timer, dueAt, cancellationToken)
            : await store.CompleteAsync(timer, clock.GetUtcNow(), cancellationToken);
    }

    [LoggerMessage(EventId = 5201, Level = LogLevel.Error, Message = "Timer {TimerId} ({Kind}) of {Module} failed; it runs again later")]
    private static partial void LogHandlerFailed(ILogger logger, Exception exception, Guid timerId, string kind, string module);

    [LoggerMessage(EventId = 5202, Level = LogLevel.Error, Message = "Timer {TimerId} of {Module} has unknown kind {Kind}; held back an hour")]
    private static partial void LogUnknownKind(ILogger logger, Guid timerId, string kind, string module);

    [LoggerMessage(EventId = 5205, Level = LogLevel.Warning, Message = "Timer {TimerId} ({Kind}) of {Module} waited too long in its batch; left for its next claimer")]
    private static partial void LogLeaseExpiring(ILogger logger, Guid timerId, string kind, string module);

    [LoggerMessage(EventId = 5203, Level = LogLevel.Warning, Message = "Timer {TimerId} ({Kind}) of {Module} was claimed by another worker before it finished")]
    private static partial void LogLeaseLost(ILogger logger, Guid timerId, string kind, string module);

    [LoggerMessage(EventId = 5206, Level = LogLevel.Critical, Message = "Timer {TimerId} ({Kind}) of {Module} failed {Failures} times in a row and is dead; clear dead_at to run it again")]
    private static partial void LogDead(ILogger logger, Guid timerId, string kind, string module, int failures);
}
