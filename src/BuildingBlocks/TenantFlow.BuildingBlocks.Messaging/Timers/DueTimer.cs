namespace TenantFlow.BuildingBlocks.Messaging.Timers;

/// <summary>
/// A claimed, due timer: what to do, for which subject, in which tenant, how often it ran before, how many of the last runs
/// failed in a row, and the lease this claim holds (exactly as stored, so finishing the timer can prove the claim is still
/// this one).
/// </summary>
public sealed record DueTimer(Guid Id, Guid TenantId, string Kind, Guid SubjectId, int Attempts, DateTimeOffset LockedUntil, int Failures);
