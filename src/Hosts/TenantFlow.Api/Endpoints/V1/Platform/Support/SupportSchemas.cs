using TenantFlow.IdentityAccess.Contracts;
using TenantFlow.ImportsExports.Contracts;
using TenantFlow.Operations.Domain.SupportGrants;

namespace TenantFlow.Api.Endpoints.V1.Platform.Support;

/// <summary>A support grant request: the tenant, a ticket reference such as <c>SUP-1234</c>, and 1 to 240 minutes.</summary>
internal sealed record OpenSupportGrantRequest(Guid? TenantId, string? Ticket, int? Minutes);

internal sealed record SupportGrantResponse(Guid Id, Guid TenantId, string Ticket, DateTimeOffset StartsAt, DateTimeOffset ExpiresAt, DateTimeOffset? EndedAt)
{
    public static SupportGrantResponse From(SupportGrant grant) => new(grant.Id, grant.TenantId, grant.Ticket, grant.StartsAt, grant.ExpiresAt, grant.EndedAt);
}

/// <summary>A membership as support sees it: no issuer or subject (ADR-0027 decision 3).</summary>
internal sealed record SupportMemberResponse(Guid MembershipId, string Role, string Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public static SupportMemberResponse From(SupportMember member) => new(member.MembershipId, member.Role, member.Status, member.CreatedAt, member.UpdatedAt);
}

/// <summary>An import or export as support sees it: status, counts and failure code, never content.</summary>
internal sealed record SupportJobResponse(Guid JobId, string Kind, string Status, int Created, int Updated, int Failed, int Rows, string? FailureCode, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt)
{
    public static SupportJobResponse From(SupportJob job) =>
        new(job.JobId, job.Kind, job.Status, job.Created, job.Updated, job.Failed, job.Rows, job.FailureCode, job.CreatedAt, job.CompletedAt);
}

internal sealed record SupportListResponse<T>(IReadOnlyList<T> Items);
