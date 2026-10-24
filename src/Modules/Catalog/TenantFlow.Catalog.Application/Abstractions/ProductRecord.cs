using TenantFlow.Catalog.Domain.Products;

namespace TenantFlow.Catalog.Application.Abstractions;

/// <summary>
/// A product with the row version the database holds for it. The version is the basis of the ETag: an edit succeeds only
/// if the row still has the version the caller read (SPEC-0003 criterion 8).
/// </summary>
public sealed record ProductRecord(Product Product, uint Version);
