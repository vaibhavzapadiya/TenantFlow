namespace TenantFlow.BuildingBlocks.Abstractions.Messaging;

/// <summary>
/// A message that can never succeed, however often it is retried: a bad envelope, an unknown tenant, type or version,
/// or an unreadable payload. It goes straight to the dead-letter queue (ADR-0021). The message says why, and never
/// repeats payload content.
/// </summary>
public sealed class InvalidMessageException : Exception
{
    public InvalidMessageException()
    {
    }

    public InvalidMessageException(string message)
        : base(message)
    {
    }

    public InvalidMessageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
