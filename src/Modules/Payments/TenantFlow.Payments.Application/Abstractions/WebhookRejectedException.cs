namespace TenantFlow.Payments.Application.Abstractions;

/// <summary>A webhook that must not be acted on: its signature or timestamp does not verify, or its body is malformed.</summary>
public sealed class WebhookRejectedException : Exception
{
    public WebhookRejectedException()
    {
    }

    public WebhookRejectedException(string message)
        : base(message)
    {
    }

    public WebhookRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public WebhookRejectedException(WebhookRejection reason)
        : base($"The webhook was rejected: {reason}.")
    {
        Reason = reason;
    }

    public WebhookRejection Reason { get; }
}
