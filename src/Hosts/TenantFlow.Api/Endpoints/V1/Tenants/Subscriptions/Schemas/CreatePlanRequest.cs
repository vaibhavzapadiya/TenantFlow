using TenantFlow.Api.Endpoints.V1.Tenants.Products.Schemas;

namespace TenantFlow.Api.Endpoints.V1.Tenants.Subscriptions.Schemas;

/// <summary>A new plan: its price per <see cref="Interval"/> (<c>month</c> or <c>year</c>) and free trial days (0 to 90, default 0).</summary>
internal sealed record CreatePlanRequest(string? Name, PriceRequest? Price, string? Interval, int? TrialDays);
