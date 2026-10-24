using TenantFlow.Tenants.Application.Lifecycle.Validators;

namespace TenantFlow.Tenants.Application.Lifecycle.Commands;

/// <summary>Input of the create-tenant use case, validated by <see cref="CreateTenantCommandValidator"/>.</summary>
public sealed record CreateTenantCommand(string? Name);
