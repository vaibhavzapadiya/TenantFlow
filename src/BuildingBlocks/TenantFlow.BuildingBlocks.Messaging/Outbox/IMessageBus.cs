namespace TenantFlow.BuildingBlocks.Messaging.Outbox;

/// <summary>The broker, as the outbox publisher sees it.</summary>
public interface IMessageBus
{
    /// <summary>
    /// Publishes the messages, in order, and returns once the broker has confirmed or refused each of them. A message is
    /// in <see cref="PublishResult.Confirmed"/> only after its confirm, so the caller never marks an unconfirmed message
    /// as published. A broker that cannot be reached fails every message.
    /// </summary>
    Task<PublishResult> PublishAsync(IReadOnlyList<PendingMessage> messages, CancellationToken cancellationToken);
}
