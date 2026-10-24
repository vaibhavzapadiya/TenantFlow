using TenantFlow.Orders.Domain.Orders;

namespace TenantFlow.Orders.Application.Abstractions;

/// <summary>What a consumed message did to an order, and the order's status afterwards (null when the order does not exist).</summary>
public sealed record MessageEffect(MessageEffectKind Kind, OrderStatus? Status);
