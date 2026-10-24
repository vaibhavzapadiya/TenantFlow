namespace TenantFlow.BuildingBlocks.Messaging.Inbox;

/// <summary>
/// A consumer's record that it processed a message id (ADR-0021). Written in the same tenant-scoped transaction as the
/// consumer's business effect, so a redelivered message finds it and changes nothing.
/// </summary>
public sealed class ProcessedMessage
{
    private ProcessedMessage(string consumer, Guid messageId, Guid tenantId, DateTimeOffset processedAt)
    {
        Consumer = consumer;
        MessageId = messageId;
        TenantId = tenantId;
        ProcessedAt = processedAt;
    }

    public string Consumer { get; private set; }

    public Guid MessageId { get; private set; }

    public Guid TenantId { get; private set; }

    public DateTimeOffset ProcessedAt { get; private set; }
}
