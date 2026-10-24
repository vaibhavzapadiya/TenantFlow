using TenantFlow.Audit.Application.Abstractions;
using TenantFlow.Audit.Application.Records.Dtos;
using TenantFlow.Audit.Contracts;
using TenantFlow.Audit.Domain.Records;
using TenantFlow.BuildingBlocks.Abstractions.Errors;
using TenantFlow.Tenants.Contracts;

namespace TenantFlow.Audit.Application.Records.Services;

/// <summary>
/// Reading a tenant's audit log (SPEC-0009 criteria 4 and 5): newest first, keyset paging, an optional action filter.
/// The tenant route passes the tenant of the caller's membership; the platform route a tenant the platform administrator
/// names, which must exist. Either way the read is scoped to that one tenant by row-level security.
/// </summary>
public sealed class AuditLog(IAuditStore store, ITenantDirectory tenants)
{
    public const int DefaultLimit = 50;

    public const int MaxLimit = 100;

    /// <exception cref="AppException">
    /// <see cref="AuditErrors.LimitInvalid"/>, <see cref="AuditErrors.CursorInvalid"/> or <see cref="AuditErrors.ActionInvalid"/>.
    /// </exception>
    public async Task<AuditPage> ListAsync(Guid tenantId, int? limit, string? cursor, string? action, CancellationToken cancellationToken)
    {
        var size = limit ?? DefaultLimit;
        if (size is < 1 or > MaxLimit)
        {
            throw new AppException(AuditErrors.LimitInvalid);
        }

        if (action is not null && !AuditActions.All.Contains(action))
        {
            throw new AppException(AuditErrors.ActionInvalid);
        }

        var after = cursor is null ? null : AuditCursorCodec.Decode(cursor);
        var rows = await store.ListAsync(tenantId, after, action, size + 1, cancellationToken);
        var page = rows.Take(size).Select(AuditRecordDetails.From).ToList();
        return new AuditPage(page, rows.Count > size ? AuditCursorCodec.Encode(page[^1]) : null);
    }

    /// <exception cref="AppException">
    /// <see cref="AuditErrors.TenantRequired"/>, <see cref="TenantContractErrors.NotFound"/>, or the errors of
    /// <see cref="ListAsync"/>.
    /// </exception>
    public async Task<AuditPage> ListForPlatformAsync(string? tenantId, int? limit, string? cursor, string? action, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(tenantId, "D", out var tenant) || tenant == Guid.Empty)
        {
            throw new AppException(AuditErrors.TenantRequired);
        }

        if (!(await tenants.GetAvailabilityAsync([tenant], cancellationToken)).ContainsKey(tenant))
        {
            throw new AppException(TenantContractErrors.NotFound);
        }

        return await ListAsync(tenant, limit, cursor, action, cancellationToken);
    }
}
