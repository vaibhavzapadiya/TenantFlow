namespace TenantFlow.BuildingBlocks.Messaging.Outbox;

/// <summary>What the broker did with a batch: the messages it confirmed, and the ones it refused or never received.</summary>
public sealed record PublishResult(IReadOnlyList<Guid> Confirmed, IReadOnlyList<PublishFailure> Failed);
