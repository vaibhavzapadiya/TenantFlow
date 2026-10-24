namespace TenantFlow.Api.Endpoints.V1.Tenants.Stock.Schemas;

/// <summary>Body of <c>POST .../products/{productId}/stock/adjustments</c>. Rules: <c>AdjustStockCommandValidator</c> (ADR-0014).</summary>
internal sealed record AdjustStockRequest(int? Delta, string? Reason);
