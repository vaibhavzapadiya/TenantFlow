using TenantFlow.Tenants.Application.Lifecycle.Dtos;

namespace TenantFlow.Api.Endpoints.V1.Tenants.Schemas;

/// <summary>A tenant as its own members see it: just what they need. Lifecycle details stay on the platform API.</summary>
internal sealed record TenantSummaryResponse(Guid Id, string Name)
{
    public static TenantSummaryResponse From(TenantDetails tenant) => new(tenant.Id, tenant.Name);
}
