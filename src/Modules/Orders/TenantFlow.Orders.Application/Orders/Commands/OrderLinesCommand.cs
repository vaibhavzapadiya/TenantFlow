namespace TenantFlow.Orders.Application.Orders.Commands;

/// <summary>
/// The lines of a new order, or the replacement lines of a draft: both use cases take the same input and the same rules.
/// </summary>
public sealed record OrderLinesCommand(IReadOnlyList<OrderLineInput?>? Lines);
