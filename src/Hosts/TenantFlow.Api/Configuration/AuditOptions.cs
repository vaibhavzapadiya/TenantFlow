namespace TenantFlow.Api.Configuration;

/// <summary>
/// The central audit log's settings, bound from <c>Audit</c> (ADR-0026 decision 3). The key turns an operator's issuer
/// and subject into a reference, so the log never stores the subject. It is a secret: user-secrets in Development
/// (appsettings.Development.json or user-secrets), the environment or a secret store elsewhere (ADR-0008).
/// </summary>
internal sealed class AuditOptions
{
    public const string SectionName = "Audit";

    /// <summary>The smallest key accepted: the HMAC-SHA256 block of security, 32 bytes.</summary>
    public const int MinimumKeyBytes = 32;

    // The message names the key, never the value, so a startup failure cannot leak it.
    public const string KeyRequiredMessage =
        "Audit:OperatorReferenceKey is required: a base64 key of at least 32 bytes. In Development it comes from appsettings.Development.json or user-secrets; elsewhere set it through the environment or a secret store.";

    /// <summary>Base64, at least <see cref="MinimumKeyBytes"/> bytes once decoded. Required.</summary>
    public string? OperatorReferenceKey { get; set; }

    /// <summary>The decoded key, or null when the setting is missing, not base64, or too short.</summary>
    public byte[]? DecodedKey()
    {
        if (string.IsNullOrWhiteSpace(OperatorReferenceKey))
        {
            return null;
        }

        var buffer = new byte[OperatorReferenceKey.Length];
        return Convert.TryFromBase64String(OperatorReferenceKey, buffer, out var written) && written >= MinimumKeyBytes ? buffer[..written] : null;
    }
}
