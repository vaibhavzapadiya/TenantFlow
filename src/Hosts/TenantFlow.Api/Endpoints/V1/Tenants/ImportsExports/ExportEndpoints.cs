using Microsoft.AspNetCore.Mvc;
using TenantFlow.Api.Endpoints.V1.TenantAccess;
using TenantFlow.Api.Endpoints.V1.Tenants.ImportsExports.Schemas;
using TenantFlow.IdentityAccess.Contracts;
using TenantFlow.ImportsExports.Application.ProductExports;

namespace TenantFlow.Api.Endpoints.V1.Tenants.ImportsExports;

/// <summary>
/// Product exports (SPEC-0008 criteria 7 to 9). Every route requires <c>catalog.manage</c>. Submitting takes an
/// <c>Idempotency-Key</c> and answers 202 with a job to poll; the Worker writes the file, which downloads for 7 days.
/// Failures (ADR-0016): 400 key; 404 <c>imports_exports.job_not_found</c>, or <c>imports_exports.file_not_ready</c> before
/// the export succeeded and after it expired; 409 <c>imports_exports.too_many_jobs</c>; 422
/// <c>imports_exports.idempotency_key_reused</c>; 503 <c>imports_exports.storage_unavailable</c> when the file cannot be
/// read from storage right now (ADR-0024 amendment 1).
/// </summary>
internal static class ExportEndpoints
{
    private const string GetExportRoute = "GetExport";

    public static IEndpointRouteBuilder MapExportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var exports = endpoints.MapGroup("/tenants/{tenantId}/exports")
            .WithTags("Exports");

        exports.MapPost("/products", static async ([FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, HttpContext http, ITenantContext tenant, LinkGenerator links, ExportService service, CancellationToken cancellationToken) =>
            {
                var job = await service.SubmitAsync(idempotencyKey, cancellationToken);
                var location = links.GetPathByName(http, GetExportRoute, new { tenantId = tenant.TenantId, exportId = job.Id });
                return TypedResults.Accepted(location, ExportJobResponse.From(job));
            })
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .RequireTenantPermission(TenantPermissions.CatalogManage);

        exports.MapGet("/{exportId}", static async (string exportId, ExportService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(ExportJobResponse.From(await service.GetAsync(JobRouteId.Parse(exportId), cancellationToken))))
            .WithName(GetExportRoute)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireTenantPermission(TenantPermissions.CatalogManage);

        // Streamed from storage as it is read; the checksum is verified at the end, and a mismatch aborts the response.
        exports.MapGet("/{exportId}/file", static async (string exportId, ExportService service, CancellationToken cancellationToken) =>
            {
                var id = JobRouteId.Parse(exportId);
                return TypedResults.Stream(await service.OpenFileAsync(id, cancellationToken), "text/csv", $"products-{id:N}.csv");
            })
            .Produces(StatusCodes.Status200OK, contentType: "text/csv")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .RequireTenantPermission(TenantPermissions.CatalogManage);

        return endpoints;
    }
}
