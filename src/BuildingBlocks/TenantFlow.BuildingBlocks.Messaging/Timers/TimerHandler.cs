using TenantFlow.BuildingBlocks.Abstractions.Workflow;

namespace TenantFlow.BuildingBlocks.Messaging.Timers;

/// <summary>
/// Handles one kind of timer, with services from a DI scope that already acts for the timer's tenant. It may call a
/// provider (no transaction is open) and must be idempotent: a lost lease or a crash runs it again.
/// </summary>
public delegate Task<TimerDecision> TimerHandler(IServiceProvider services, DueTimer timer, CancellationToken cancellationToken);
