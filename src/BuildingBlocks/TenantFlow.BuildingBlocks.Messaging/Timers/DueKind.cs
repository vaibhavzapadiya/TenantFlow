namespace TenantFlow.BuildingBlocks.Messaging.Timers;

/// <summary>How many timers of one kind are due, and since when the oldest of them is.</summary>
public sealed record DueKind(string Kind, long Due, DateTimeOffset Oldest);
