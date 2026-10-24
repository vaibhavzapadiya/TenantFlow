using Microsoft.AspNetCore.Mvc;
using TenantFlow.Api.Endpoints.V1.TenantAccess;
using TenantFlow.Api.Endpoints.V1.Tenants.Products.Schemas;
using TenantFlow.Catalog.Application.Products.Commands;
using TenantFlow.Catalog.Application.Products.Services;
using TenantFlow.Catalog.Domain.Products;
using TenantFlow.IdentityAccess.Contracts;

namespace TenantFlow.Api.Endpoints.V1.Tenants.Products;

/// <summary>
/// The tenant's product routes (SPEC-0003 criteria 5 to 10 and 17). Each declares the permission it needs through
/// <c>RequireTenantPermission</c>, so by the time a handler runs the tenant context is set; handlers act on that
/// context's tenant and never on the route's text. Failures are thrown as <c>AppException</c> or validation errors
/// (ADR-0016): 400 invalid input, cursor, limit or missing <c>If-Match</c>; 404 <c>catalog.product_not_found</c>;
/// 409 <c>catalog.sku_taken</c>; 412 <c>catalog.version_conflict</c>.
/// </summary>
internal static class ProductEndpoints
{
    // The Location header of a created product is generated from this route name, so it always carries the version.
    private const string GetProductRoute = "GetProduct";

    public static IEndpointRouteBuilder MapProductEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var products = endpoints.MapGroup("/tenants/{tenantId}/products")
            .WithTags("Products");

        products.MapGet("/", static async (int? limit, string? cursor, string? status, string? q, ProductService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(ProductPageResponse.From(await service.ListAsync(limit, cursor, status, q, cancellationToken))))
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .RequireTenantPermission(TenantPermissions.CatalogRead);

        products.MapPost("/", static async (CreateProductRequest request, HttpContext http, ITenantContext tenant, LinkGenerator links, ProductService service, CancellationToken cancellationToken) =>
            {
                var product = await service.CreateAsync(
                    new CreateProductCommand(request.Sku, request.Name, request.Price?.Amount, request.Price?.Currency), cancellationToken);
                http.Response.Headers.ETag = RowVersionETag.Format(product.Version);
                // A relative Location from the named route (not CreatedAtRoute, which builds an absolute URL from the Host header).
                var location = links.GetPathByName(http, GetProductRoute, new { tenantId = tenant.TenantId, productId = product.Id });
                return TypedResults.Created(location, ProductResponse.From(product));
            })
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .RequireTenantPermission(TenantPermissions.CatalogManage);

        products.MapGet("/{productId}", static async (string productId, HttpContext http, ProductService service, CancellationToken cancellationToken) =>
            {
                var product = await service.GetAsync(ProductRouteId.Parse(productId), cancellationToken);
                http.Response.Headers.ETag = RowVersionETag.Format(product.Version);
                return TypedResults.Ok(ProductResponse.From(product));
            })
            .WithName(GetProductRoute)
            .RequireTenantPermission(TenantPermissions.CatalogRead);

        products.MapPut("/{productId}", static async (string productId, UpdateProductRequest request, [FromHeader(Name = "If-Match")] string? ifMatch, HttpContext http, ProductService service, CancellationToken cancellationToken) =>
            {
                var product = await service.UpdateAsync(
                    ProductRouteId.Parse(productId),
                    new UpdateProductCommand(request.Name, request.Price?.Amount, request.Price?.Currency, request.Status),
                    RowVersionETag.Parse(ifMatch, ProductErrors.IfMatchRequired),
                    cancellationToken);
                http.Response.Headers.ETag = RowVersionETag.Format(product.Version);
                return TypedResults.Ok(ProductResponse.From(product));
            })
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed)
            .RequireTenantPermission(TenantPermissions.CatalogManage);

        return endpoints;
    }
}
