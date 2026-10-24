namespace TenantFlow.Orders.Contracts.Events;

/// <summary>An order total in an event: a decimal amount and its ISO 4217 currency (AGENTS.md rule 4).</summary>
public sealed record OrderTotal(decimal Amount, string Currency);
