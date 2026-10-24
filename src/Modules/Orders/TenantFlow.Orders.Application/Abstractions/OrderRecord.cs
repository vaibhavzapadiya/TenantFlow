using TenantFlow.Orders.Domain.Orders;

namespace TenantFlow.Orders.Application.Abstractions;

/// <summary>An order with the row version the database holds for it: the basis of its ETag (SPEC-0004 criterion 8).</summary>
public sealed record OrderRecord(Order Order, uint Version);
