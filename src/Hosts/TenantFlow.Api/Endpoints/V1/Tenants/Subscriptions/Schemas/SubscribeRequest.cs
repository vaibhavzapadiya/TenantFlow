namespace TenantFlow.Api.Endpoints.V1.Tenants.Subscriptions.Schemas;

/// <summary>Subscribe to a plan, charged with the provider's payment-method token (for example <c>sim_success</c>); card data is never accepted.</summary>
internal sealed record SubscribeRequest(Guid? PlanId, string? PaymentMethod);
