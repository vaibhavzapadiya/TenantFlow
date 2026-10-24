using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using TenantFlow.Api.Configuration;
using TenantFlow.Audit.Contracts;

namespace TenantFlow.Api.Auditing;

/// <summary>
/// Turns a platform operator into the reference the central audit log stores (ADR-0026 decision 3): a lowercase hex
/// HMAC-SHA256 of issuer, a line feed and subject, under <c>Audit:OperatorReferenceKey</c>. The same operator always gets
/// the same reference, so entries can be followed per operator; without the key a reference cannot be turned back into a
/// subject, and the log alone never names anyone.
/// </summary>
/// <remarks>
/// Computed here, in the host, rather than in a module: the key is host configuration, and the modules that build audit
/// events (Tenants, IdentityAccess) then receive a finished <see cref="AuditActor"/> and never see a key.
/// </remarks>
internal sealed class OperatorReferences(IOptions<AuditOptions> options)
{
    // Validated at startup (AddTenantFlowAuditing), so a running host always has a usable key.
    private readonly byte[] _key = options.Value.DecodedKey() ?? throw new InvalidOperationException(AuditOptions.KeyRequiredMessage);

    /// <summary>The audit actor of the platform operator who sent this request.</summary>
    /// <remarks>The platform scheme guarantees both claims (issuer validation and the required subject).</remarks>
    public AuditActor PlatformAdmin(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return AuditActor.PlatformAdmin(Reference(user.FindFirst("iss")!.Value, user.FindFirst("sub")!.Value));
    }

    /// <summary>The audit actor of the support operator who sent this request (ADR-0027).</summary>
    public AuditActor SupportOperator(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return AuditActor.SupportOperator(Reference(user.FindFirst("iss")!.Value, user.FindFirst("sub")!.Value));
    }

    /// <summary>The reference for an issuer and subject.</summary>
    public string Reference(string issuer, string subject)
    {
        var hash = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(issuer + "\n" + subject));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
