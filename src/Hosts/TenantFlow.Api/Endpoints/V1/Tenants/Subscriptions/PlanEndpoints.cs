using TenantFlow.Api.Endpoints.V1.TenantAccess;
using TenantFlow.Api.Endpoints.V1.Tenants.Subscriptions.Schemas;
using TenantFlow.IdentityAccess.Contracts;
using TenantFlow.Subscriptions.Application.Plans.Commands;
using TenantFlow.Subscriptions.Application.Plans.Services;

namespace TenantFlow.Api.Endpoints.V1.Tenants.Subscriptions;

/// <summary>
/// A tenant's plans (SPEC-0007 criterion 1). Creating requires <c>subscriptions.manage</c>; listing the active plans
/// requires <c>subscriptions.subscribe</c>. Failures (ADR-0016): 400 with field errors for a broken input rule.
/// </summary>
internal static class PlanEndpoints
{
    public static IEndpointRouteBuilder MapPlanEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var plans = endpoints.MapGroup("/tenants/{tenantId}/plans")
            .WithTags("Plans");

        plans.MapPost("/", static async (CreatePlanRequest request, PlanService service, CancellationToken cancellationToken) =>
            {
                var plan = await service.CreateAsync(new CreatePlanCommand(request.Name, request.Price?.Amount, request.Price?.Currency, request.Interval, request.TrialDays ?? 0), cancellationToken);
                return TypedResults.Created((string?)null, PlanResponse.From(plan));
            })
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .RequireTenantPermission(TenantPermissions.SubscriptionsManage);

        plans.MapGet("/", static async (PlanService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(new PlanListResponse([.. (await service.ListAsync(cancellationToken)).Select(PlanResponse.From)])))
            .RequireTenantPermission(TenantPermissions.SubscriptionsSubscribe);

        return endpoints;
    }
}
