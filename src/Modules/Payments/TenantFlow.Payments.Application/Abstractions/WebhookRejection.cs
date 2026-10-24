namespace TenantFlow.Payments.Application.Abstractions;

/// <summary>Why a webhook was rejected.</summary>
public enum WebhookRejection
{
    /// <summary>No signature, an unreadable one, none that matches, or no secret configured (fail closed).</summary>
    Signature = 1,

    /// <summary>Signed outside the tolerance (5 minutes either way): possibly a replay.</summary>
    Timestamp = 2,

    /// <summary>Correctly signed, but the body is not an event TenantFlow can read.</summary>
    Malformed = 3,
}
