using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TenantFlow.BuildingBlocks.Abstractions.Workflow;
using TenantFlow.BuildingBlocks.Messaging.Timers;

namespace TenantFlow.Database.Tests.Messaging;

/// <summary>
/// ADR-0022 decision 7 against real PostgreSQL: a due timer is claimed by one runner at a time, comes back when its lease
/// passes (a crashed worker), and only the current claim may finish it, so a stale worker cannot complete or move a timer
/// another worker now runs. Uses the orders module's timer table; the timer rows are this test's own.
/// </summary>
public sealed class WorkflowTimerTests(TenantDatabaseFixture fixture) : IClassFixture<TenantDatabaseFixture>
{
    [Fact]
    public async Task A_due_timer_has_one_claimer_until_its_lease_passes_and_a_lost_claim_cannot_finish_it()
    {
        await using var dataSource = NpgsqlDataSource.Create(fixture.Database.AppConnectionString);
        var first = new WorkflowTimerStore(dataSource, "orders");
        var second = new WorkflowTimerStore(dataSource, "orders");
        var now = new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero).AddTicks(Random.Shared.Next(1, 9_999_999));
        var subject = Guid.CreateVersion7();
        await InsertAsync(dataSource, subject, dueAt: now.AddMinutes(-1));
        await InsertAsync(dataSource, Guid.CreateVersion7(), dueAt: now.AddMinutes(10));

        var claimed = Own(await first.ClaimAsync(50, now, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken), subject);
        var whileHeld = Own(await second.ClaimAsync(50, now.AddMinutes(1), TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken), subject);
        var afterLease = Own(await second.ClaimAsync(50, now.AddMinutes(3), TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken), subject);
        var staleFinished = await first.CompleteAsync(Assert.Single(claimed), now.AddMinutes(3), TestContext.Current.CancellationToken);
        var staleMoved = await first.RescheduleAsync(claimed[0], now.AddHours(1), TestContext.Current.CancellationToken);
        var finished = await second.CompleteAsync(Assert.Single(afterLease), now.AddMinutes(3), TestContext.Current.CancellationToken);
        var afterCompletion = Own(await first.ClaimAsync(50, now.AddDays(1), TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken), subject);

        Assert.Empty(whileHeld);
        Assert.Equal((false, false, true), (staleFinished, staleMoved, finished));
        Assert.Empty(afterCompletion);
    }

    // M6 review fix 5: timers of a batch run one after another; one whose lease ran out while it waited is left alone.
    [Fact]
    public async Task A_timer_whose_lease_passed_while_it_waited_in_the_batch_is_left_for_its_next_claimer()
    {
        await using var dataSource = NpgsqlDataSource.Create(fixture.Database.AppConnectionString);
        var clock = new FakeTimeProvider(new DateTimeOffset(2031, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var (first, second) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await InsertAsync(dataSource, first, clock.GetUtcNow().AddMinutes(-2), "test.slow");
        await InsertAsync(dataSource, second, clock.GetUtcNow().AddMinutes(-1), "test.slow");
        var ran = new List<Guid>();
        // Each run takes as long as the whole lease, as a hung provider call would.
        var handlers = new WorkflowTimerHandlers(static (_, _) => { }, [new("test.slow", (_, timer, _) =>
        {
            ran.Add(timer.SubjectId);
            clock.Advance(WorkflowTimerRunner.Lease);
            return Task.FromResult(TimerDecision.Complete);
        })]);
        await using var services = new ServiceCollection().BuildServiceProvider();
        var runner = new WorkflowTimerRunner([new WorkflowTimerStore(dataSource, "orders")], services.GetRequiredService<IServiceScopeFactory>(), handlers, clock, NullLogger<WorkflowTimerRunner>.Instance);

        await runner.RunDueAsync(TestContext.Current.CancellationToken);

        Assert.Equal([first], ran.Where(id => id == first || id == second));
        Assert.Equal((true, false), (await CompletedAsync(dataSource, first), await CompletedAsync(dataSource, second)));
    }

    // SPEC-0007 criterion 8 (ADR-0023 decision 1): a row whose handler keeps throwing is dead after 20 failures in a row and
    // never claimed again; another row of the same table keeps running.
    [Fact]
    public async Task A_timer_whose_handler_fails_twenty_times_in_a_row_is_dead_and_the_others_keep_running()
    {
        await using var dataSource = NpgsqlDataSource.Create(fixture.Database.AppConnectionString);
        var clock = new FakeTimeProvider(new DateTimeOffset(2032, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var (poison, healthy) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await InsertAsync(dataSource, poison, clock.GetUtcNow().AddMinutes(-1), "test.poison");
        var handlers = new WorkflowTimerHandlers(static (_, _) => { },
        [
            new("test.poison", static (_, _, _) => throw new InvalidOperationException("Poison.")),
            new("test.healthy", static (_, _, _) => Task.FromResult(TimerDecision.Complete)),
        ]);
        await using var services = new ServiceCollection().BuildServiceProvider();
        var runner = new WorkflowTimerRunner([new WorkflowTimerStore(dataSource, "orders")], services.GetRequiredService<IServiceScopeFactory>(), handlers, clock, NullLogger<WorkflowTimerRunner>.Instance);

        var failures = new List<(int Failures, bool Dead)>();
        for (var run = 0; run < WorkflowTimerRunner.MaxFailures; run++)
        {
            await runner.RunDueAsync(TestContext.Current.CancellationToken);
            failures.Add(await FailuresAsync(dataSource, poison));
            clock.Advance(TimeSpan.FromMinutes(61));
        }

        await InsertAsync(dataSource, healthy, clock.GetUtcNow().AddMinutes(-1), "test.healthy");
        clock.Advance(TimeSpan.FromDays(1));
        await runner.RunDueAsync(TestContext.Current.CancellationToken);

        Assert.Equal([.. Enumerable.Range(1, 19).Select(static n => (n, false)), (20, true)], failures);
        Assert.Equal((20, true), await FailuresAsync(dataSource, poison));
        Assert.Equal((true, false), (await CompletedAsync(dataSource, healthy), await CompletedAsync(dataSource, poison)));
        // SPEC-0010 criterion 3: the dead-timer gauge's count.
        Assert.Equal(1, await new WorkflowTimerStore(dataSource, "orders").CountDeadAsync(TestContext.Current.CancellationToken));
    }

    // SPEC-0010 criterion 4: a timer that ran and was rescheduled is unresolved; its subject's version 7 id dates it.
    [Fact]
    public async Task A_rescheduled_timer_counts_as_unresolved_since_its_subject_was_created()
    {
        await using var dataSource = NpgsqlDataSource.Create(fixture.Database.AppConnectionString);
        var clock = new FakeTimeProvider(new DateTimeOffset(2033, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var created = clock.GetUtcNow().AddHours(-3);
        var (waiting, notYetRun) = (Guid.CreateVersion7(created), Guid.CreateVersion7(created.AddHours(-1)));
        await InsertAsync(dataSource, waiting, clock.GetUtcNow().AddMinutes(-1), "test.unresolved");
        await InsertAsync(dataSource, notYetRun, clock.GetUtcNow().AddDays(1), "test.unresolved");
        var handlers = new WorkflowTimerHandlers(static (_, _) => { },
            [new("test.unresolved", (_, _, _) => Task.FromResult(TimerDecision.RunAgain(clock.GetUtcNow().AddMinutes(1))))]);
        await using var services = new ServiceCollection().BuildServiceProvider();
        var store = new WorkflowTimerStore(dataSource, "orders");
        var runner = new WorkflowTimerRunner([store], services.GetRequiredService<IServiceScopeFactory>(), handlers, clock, NullLogger<WorkflowTimerRunner>.Instance);

        await runner.RunDueAsync(TestContext.Current.CancellationToken);
        var unresolved = (await store.UnresolvedByKindAsync(TestContext.Current.CancellationToken)).Single(static u => u.Kind == "test.unresolved");

        Assert.Equal(1, unresolved.Due);
        Assert.Equal(created, unresolved.Oldest, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task Scheduling_a_kind_and_subject_again_moves_the_timer_only_within_its_own_tenant()
    {
        await using var dataSource = NpgsqlDataSource.Create(fixture.Database.AppConnectionString);
        var (subject, owner, other) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        // PostgreSQL keeps microseconds.
        var now = DateTimeOffset.UtcNow;
        var first = new DateTimeOffset(now.Ticks - (now.Ticks % 10), TimeSpan.Zero).AddDays(1);

        await ScheduleAsync(dataSource, owner, subject, first);
        await ScheduleAsync(dataSource, other, subject, first.AddDays(1));
        var afterOther = await TimerAsync(dataSource, subject);
        await ScheduleAsync(dataSource, owner, subject, first.AddDays(2));

        Assert.Equal((owner, first), (afterOther.Tenant, afterOther.DueAt));
        Assert.Equal((owner, first.AddDays(2)), await TimerAsync(dataSource, subject));
    }

    private static async Task ScheduleAsync(NpgsqlDataSource dataSource, Guid tenant, Guid subject, DateTimeOffset dueAt)
    {
        // The statement's {0} to {4} placeholders as Npgsql's positional parameters.
        var sql = Enumerable.Range(0, 5).Aggregate(WorkflowTimerSql.Schedule("orders"), static (text, i) => text.Replace($"{{{i}}}", $"${i + 1}", StringComparison.Ordinal));
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter { Value = Guid.CreateVersion7() });
        command.Parameters.Add(new NpgsqlParameter { Value = tenant });
        command.Parameters.Add(new NpgsqlParameter { Value = "test.schedule" });
        command.Parameters.Add(new NpgsqlParameter { Value = subject });
        command.Parameters.Add(new NpgsqlParameter { Value = dueAt });
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<(Guid Tenant, DateTimeOffset DueAt)> TimerAsync(NpgsqlDataSource dataSource, Guid subject)
    {
        await using var command = dataSource.CreateCommand("SELECT tenant_id, due_at FROM orders.workflow_timers WHERE kind = 'test.schedule' AND subject_id = @subject");
        command.Parameters.AddWithValue("subject", subject);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        return (reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1));
    }

    private static async Task<(int Failures, bool Dead)> FailuresAsync(NpgsqlDataSource dataSource, Guid subject)
    {
        await using var command = dataSource.CreateCommand("SELECT failures, dead_at IS NOT NULL FROM orders.workflow_timers WHERE subject_id = @subject");
        command.Parameters.AddWithValue("subject", subject);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        return (reader.GetInt32(0), reader.GetBoolean(1));
    }

    private static async Task<bool> CompletedAsync(NpgsqlDataSource dataSource, Guid subject)
    {
        await using var command = dataSource.CreateCommand("SELECT completed_at IS NOT NULL FROM orders.workflow_timers WHERE subject_id = @subject");
        command.Parameters.AddWithValue("subject", subject);
        return (bool)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static List<DueTimer> Own(IReadOnlyList<DueTimer> claimed, Guid subject) => [.. claimed.Where(t => t.SubjectId == subject)];

    private static async Task InsertAsync(NpgsqlDataSource dataSource, Guid subject, DateTimeOffset dueAt, string kind = "test.kind")
    {
        await using var command = dataSource.CreateCommand(
            "INSERT INTO orders.workflow_timers (id, tenant_id, kind, subject_id, due_at) VALUES (@id, @tenant, @kind, @subject, @due)");
        command.Parameters.AddWithValue("kind", kind);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("tenant", Guid.CreateVersion7());
        command.Parameters.AddWithValue("subject", subject);
        command.Parameters.AddWithValue("due", dueAt);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
