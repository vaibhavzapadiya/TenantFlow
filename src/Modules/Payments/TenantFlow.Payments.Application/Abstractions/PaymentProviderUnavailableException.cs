namespace TenantFlow.Payments.Application.Abstractions;

/// <summary>
/// The provider gave no usable answer: it timed out, failed with a 5xx, or sent something unreadable. The charge may or
/// may not have happened, so the caller records the outcome as unknown and reconciles it; it never retries with a new key.
/// </summary>
public sealed class PaymentProviderUnavailableException : Exception
{
    public PaymentProviderUnavailableException()
    {
    }

    public PaymentProviderUnavailableException(string message)
        : base(message)
    {
    }

    public PaymentProviderUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
