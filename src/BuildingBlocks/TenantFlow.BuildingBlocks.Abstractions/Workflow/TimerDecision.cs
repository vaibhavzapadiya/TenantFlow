namespace TenantFlow.BuildingBlocks.Abstractions.Workflow;

/// <summary>What a workflow timer's handler decided (ADR-0022 decision 7): the work is done, or it runs again at a later time.</summary>
public sealed record TimerDecision(DateTimeOffset? RunAgainAt)
{
    /// <summary>The work is done; the timer never runs again.</summary>
    public static readonly TimerDecision Complete = new((DateTimeOffset?)null);

    /// <summary>Run again at <paramref name="dueAt"/>.</summary>
    public static TimerDecision RunAgain(DateTimeOffset dueAt) => new(dueAt);
}
