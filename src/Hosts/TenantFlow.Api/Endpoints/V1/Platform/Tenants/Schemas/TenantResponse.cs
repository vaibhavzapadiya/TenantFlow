using TenantFlow.Tenants.Application.Lifecycle.Dtos;
using TenantFlow.Tenants.Domain.Tenants;

namespace TenantFlow.Api.Endpoints.V1.Platform.Tenants.Schemas;

/// <summary>A tenant as the platform API returns it. Status is a stable lowercase word.</summary>
internal sealed record TenantResponse(Guid Id, string Name, string Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static TenantResponse From(TenantDetails tenant) =>
        new(tenant.Id, tenant.Name, tenant.Status.ToName(), tenant.CreatedAt, tenant.UpdatedAt);
}
