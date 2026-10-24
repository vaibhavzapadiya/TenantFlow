namespace TenantFlow.Subscriptions.Application.Subscriptions.Commands;

/// <summary>Subscribe the caller to a plan, charged with a provider payment-method token.</summary>
public sealed record SubscribeCommand(Guid PlanId, string? PaymentMethod);
