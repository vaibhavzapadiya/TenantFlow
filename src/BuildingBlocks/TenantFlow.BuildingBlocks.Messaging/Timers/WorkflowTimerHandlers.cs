namespace TenantFlow.BuildingBlocks.Messaging.Timers;

/// <summary>
/// Every timer kind the host runs, and how a scope is made to act for a timer's tenant. The host composes it from the
/// modules' own handler lists; the tenant step is the host's, because only IdentityAccess may set a tenant context.
/// </summary>
public sealed class WorkflowTimerHandlers
{
    private readonly Dictionary<string, TimerHandler> _handlers;

    public WorkflowTimerHandlers(Action<IServiceProvider, Guid> enterTenant, IEnumerable<KeyValuePair<string, TimerHandler>> handlers)
    {
        ArgumentNullException.ThrowIfNull(enterTenant);
        ArgumentNullException.ThrowIfNull(handlers);
        EnterTenant = enterTenant;
        _handlers = new Dictionary<string, TimerHandler>(handlers, StringComparer.Ordinal);
    }

    /// <summary>Makes a fresh scope act for one tenant.</summary>
    public Action<IServiceProvider, Guid> EnterTenant { get; }

    public TimerHandler? Find(string kind) => _handlers.GetValueOrDefault(kind);
}
