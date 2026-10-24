namespace TenantFlow.BuildingBlocks.Messaging.Timers;

/// <summary>
/// A persisted deadline of a workflow (ADR-0022 decision 7): "do <see cref="Kind"/> for <see cref="SubjectId"/> in
/// <see cref="TenantId"/> at <see cref="DueAt"/>". Written by the owning module in the same transaction as the state change
/// that needs it, so a deadline exists if and only if that change committed. Ids only, so it is not under row-level
/// security, like the outbox; the work itself runs in the timer's tenant under RLS.
/// </summary>
public sealed class WorkflowTimer
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>What to do, for example <c>payments.reconcile</c>; one timer per kind and subject.</summary>
    public string Kind { get; set; } = string.Empty;

    public Guid SubjectId { get; set; }

    public DateTimeOffset DueAt { get; set; }

    /// <summary>How many times the handler has run for it.</summary>
    public int Attempts { get; set; }

    /// <summary>Claimed by a worker until then; another may claim it once this passes.</summary>
    public DateTimeOffset? LockedUntil { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>How many runs in a row ended with the handler throwing; reset by a run that finishes (ADR-0023).</summary>
    public int Failures { get; set; }

    /// <summary>Set when the handler failed <see cref="WorkflowTimerRunner.MaxFailures"/> times in a row: never claimed again until cleared.</summary>
    public DateTimeOffset? DeadAt { get; set; }
}
