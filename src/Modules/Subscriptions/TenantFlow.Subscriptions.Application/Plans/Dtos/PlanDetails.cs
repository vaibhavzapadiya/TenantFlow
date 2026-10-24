using TenantFlow.Subscriptions.Domain.Plans;

namespace TenantFlow.Subscriptions.Application.Plans.Dtos;

/// <summary>A plan as callers see it.</summary>
public sealed record PlanDetails(Guid Id, string Name, decimal Amount, string Currency, string Interval, int TrialDays, DateTimeOffset CreatedAt)
{
    public static PlanDetails From(Plan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new PlanDetails(plan.Id, plan.Name, plan.Amount, plan.Currency, plan.Interval.ToName(), plan.TrialDays, plan.CreatedAt);
    }
}
