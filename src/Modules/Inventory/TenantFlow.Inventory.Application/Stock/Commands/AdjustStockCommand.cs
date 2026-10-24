namespace TenantFlow.Inventory.Application.Stock.Commands;

/// <summary>Input of the adjust-stock use case, validated by <c>AdjustStockCommandValidator</c>.</summary>
public sealed record AdjustStockCommand(int? Delta, string? Reason);
