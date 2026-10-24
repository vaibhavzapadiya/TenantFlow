namespace TenantFlow.Subscriptions.Application.Plans.Commands;

/// <summary>Create a plan: a price per month or year, and free trial days (0 for none).</summary>
public sealed record CreatePlanCommand(string? Name, decimal? Amount, string? Currency, string? Interval, int? TrialDays);
