using Microsoft.AspNetCore.Mvc;
using TenantFlow.Api.Endpoints.V1.TenantAccess;
using TenantFlow.Api.Endpoints.V1.Tenants.ImportsExports.Schemas;
using TenantFlow.IdentityAccess.Contracts;
using TenantFlow.ImportsExports.Application.ProductImports;

namespace TenantFlow.Api.Endpoints.V1.Tenants.ImportsExports;

/// <summary>
/// Product imports (SPEC-0008 criteria 3 and 6). Every route requires <c>catalog.manage</c>. Submitting takes a multipart
/// <c>file</c> (CSV, at most 5 MB) and an <c>Idempotency-Key</c> and answers 202 with a job to poll; the Worker does the
/// work. Failures (ADR-0016): 400 key or file; 404 <c>imports_exports.job_not_found</c>; 409
/// <c>imports_exports.too_many_jobs</c>; 413 <c>imports_exports.file_too_large</c>; 422
/// <c>imports_exports.idempotency_key_reused</c>.
/// </summary>
internal static class ImportEndpoints
{
    private const string GetImportRoute = "GetImport";

    public static IEndpointRouteBuilder MapImportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var imports = endpoints.MapGroup("/tenants/{tenantId}/imports")
            .WithTags("Imports");

        imports.MapPost("/products", static async ([FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, HttpContext http, ITenantContext tenant, LinkGenerator links, ImportService service, CancellationToken cancellationToken) =>
            {
                // The key first, so a request that will be refused does not cost reading its upload.
                ImportService.CheckIdempotencyKey(idempotencyKey);
                // The form is read here, not bound as a parameter: binding runs before the tenant filter, so a malformed form
                // would be answered (400) before a caller who may not import is refused (401, 403 or 404).
                var form = http.Request.HasFormContentType ? await http.Request.ReadFormAsync(cancellationToken) : null;
                var file = form?.Files.GetFile("file");
                await using var content = file?.OpenReadStream() ?? Stream.Null;
                var job = await service.SubmitAsync(content, file?.ContentType, idempotencyKey, cancellationToken);
                var location = links.GetPathByName(http, GetImportRoute, new { tenantId = tenant.TenantId, importId = job.Id });
                return TypedResults.Accepted(location, ImportJobResponse.From(job));
            })
            // Bearer tokens, not cookies: no ambient credential for a forged form to ride on (ADR-0009).
            .DisableAntiforgery()
            // This route alone reads more than the server-wide 64 KiB (RequestLimits): the 5 MB file and its multipart framing,
            // in a form of a few parts. Routing applies both limits before the body is read.
            .WithMetadata(new RequestSizeLimitAttribute(ImportLimits.MaxRequestBytes))
            .WithFormOptions(multipartBodyLengthLimit: ImportLimits.MaxRequestBytes, valueCountLimit: ImportLimits.MaxFormParts)
            .Accepts<IFormFile>("multipart/form-data")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .RequireTenantPermission(TenantPermissions.CatalogManage);

        imports.MapGet("/{importId}", static async (string importId, ImportService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(ImportJobResponse.From(await service.GetAsync(JobRouteId.Parse(importId), cancellationToken))))
            .WithName(GetImportRoute)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireTenantPermission(TenantPermissions.CatalogManage);

        imports.MapGet("/{importId}/errors", static async (string importId, ImportService service, CancellationToken cancellationToken) =>
                TypedResults.Ok(new ImportErrorsResponse([.. (await service.ErrorsAsync(JobRouteId.Parse(importId), cancellationToken)).Select(static e => new ImportErrorResponse(e.Row, e.Column, e.Code))])))
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireTenantPermission(TenantPermissions.CatalogManage);

        return endpoints;
    }
}
