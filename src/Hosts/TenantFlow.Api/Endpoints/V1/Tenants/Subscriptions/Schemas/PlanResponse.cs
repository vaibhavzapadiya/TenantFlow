using TenantFlow.Api.Endpoints.V1.Tenants.Orders.Schemas;
using TenantFlow.Subscriptions.Application.Plans.Dtos;

namespace TenantFlow.Api.Endpoints.V1.Tenants.Subscriptions.Schemas;

/// <summary>A plan as the API returns it.</summary>
internal sealed record PlanResponse(Guid Id, string Name, MoneyResponse Price, string Interval, int TrialDays, DateTimeOffset CreatedAt)
{
    public static PlanResponse From(PlanDetails plan) =>
        new(plan.Id, plan.Name, new MoneyResponse(plan.Amount, plan.Currency), plan.Interval, plan.TrialDays, plan.CreatedAt);
}

/// <summary>The tenant's active plans, oldest first (at most 100).</summary>
internal sealed record PlanListResponse(IReadOnlyList<PlanResponse> Items);
