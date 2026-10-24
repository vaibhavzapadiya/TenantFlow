namespace TenantFlow.Orders.Application.Abstractions;

/// <summary>What <see cref="IOrderStore.ApplyMessageAsync"/> did.</summary>
public enum MessageEffectKind
{
    /// <summary>The order moved; the message is recorded.</summary>
    Applied = 1,

    /// <summary>The state table does not allow the trigger now (late or out of order); the message is recorded, the order unchanged.</summary>
    Ignored = 2,

    /// <summary>This consumer processed the message id before; nothing was written.</summary>
    Duplicate = 3,

    /// <summary>The tenant has no such order; nothing was written.</summary>
    OrderMissing = 4,
}
