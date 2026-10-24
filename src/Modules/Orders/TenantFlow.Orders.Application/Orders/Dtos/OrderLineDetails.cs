namespace TenantFlow.Orders.Application.Orders.Dtos;

/// <summary>An order line as the use cases return it: the snapshot taken when the line was set.</summary>
public sealed record OrderLineDetails(Guid ProductId, string Sku, string Name, decimal UnitPrice, int Quantity, decimal LineTotal);
