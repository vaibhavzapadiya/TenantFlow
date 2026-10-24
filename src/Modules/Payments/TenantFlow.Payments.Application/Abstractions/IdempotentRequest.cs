namespace TenantFlow.Payments.Application.Abstractions;

/// <summary>
/// A request stored under an idempotency key: its hash, and its response once it finished (null while it runs), as the
/// JSON of the operation's own result (a payment or a refund).
/// </summary>
public sealed record IdempotentRequest(string RequestHash, string? ResponseJson);
