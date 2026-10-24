namespace TenantFlow.Payments.Contracts.Events;

/// <summary>A payment's amount in an event: a decimal amount and an ISO 4217 currency (AGENTS.md rule 4).</summary>
public sealed record PaymentAmount(decimal Amount, string Currency);
