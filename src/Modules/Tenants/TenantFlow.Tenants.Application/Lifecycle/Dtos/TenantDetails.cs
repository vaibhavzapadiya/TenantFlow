using TenantFlow.Tenants.Domain.Tenants;

namespace TenantFlow.Tenants.Application.Lifecycle.Dtos;

/// <summary>Tenant details returned by the lifecycle use cases.</summary>
public sealed record TenantDetails(Guid Id, string Name, TenantStatus Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    internal static TenantDetails From(Tenant tenant) =>
        new(tenant.Id, tenant.Name, tenant.Status, tenant.CreatedAt, tenant.UpdatedAt);
}
