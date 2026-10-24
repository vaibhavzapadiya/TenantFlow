namespace TenantFlow.Payments.Application.Abstractions;

/// <summary>An idempotency key of the current tenant, scoped to one operation (ADR-0022 decision 4).</summary>
public sealed record IdempotencyKeyRef(string Operation, string Key);
