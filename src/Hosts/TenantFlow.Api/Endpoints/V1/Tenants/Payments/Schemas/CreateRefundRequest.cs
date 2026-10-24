namespace TenantFlow.Api.Endpoints.V1.Tenants.Payments.Schemas;

/// <summary>A refund: the amount, in the payment's currency (a decimal, never a float, AGENTS.md rule 4), and why.</summary>
internal sealed record CreateRefundRequest(decimal? Amount, string? Reason);
