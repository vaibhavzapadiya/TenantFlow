namespace TenantFlow.Orders.Application.Orders.Commands;

/// <summary>One requested line: which product and how many. Nullable, because input is checked, not trusted.</summary>
public sealed record OrderLineInput(Guid? ProductId, int? Quantity);
