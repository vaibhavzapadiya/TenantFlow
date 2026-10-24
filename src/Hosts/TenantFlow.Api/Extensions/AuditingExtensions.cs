using TenantFlow.Api.Auditing;
using TenantFlow.Api.Configuration;

namespace TenantFlow.Api.Extensions;

/// <summary>The API's part of the central audit log (ADR-0026): the operator-reference key and what uses it.</summary>
internal static class AuditingExtensions
{
    /// <summary>Requires a usable <c>Audit:OperatorReferenceKey</c> before the host serves anything.</summary>
    public static WebApplicationBuilder AddTenantFlowAuditing(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddOptions<AuditOptions>()
            .BindConfiguration(AuditOptions.SectionName)
            .Validate(static options => options.DecodedKey() is not null, AuditOptions.KeyRequiredMessage)
            .ValidateOnStart();
        builder.Services.AddSingleton<OperatorReferences>();
        return builder;
    }
}
