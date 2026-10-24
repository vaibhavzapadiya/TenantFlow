namespace TenantFlow.BuildingBlocks.Messaging.Outbox;

/// <summary>A message the broker did not confirm, and why.</summary>
public sealed record PublishFailure(Guid MessageId, Exception Error);
