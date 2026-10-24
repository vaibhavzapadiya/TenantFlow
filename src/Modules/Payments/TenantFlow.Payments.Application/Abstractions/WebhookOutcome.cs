namespace TenantFlow.Payments.Application.Abstractions;

/// <summary>What a verified webhook did.</summary>
public enum WebhookOutcome
{
    /// <summary>Applied by the state table (the status may or may not have changed).</summary>
    Applied = 1,

    /// <summary>Recorded, but the state table does not allow it now (late or out of order), or its type is not acted on.</summary>
    Ignored = 2,

    /// <summary>The event id was seen before; nothing changed.</summary>
    Duplicate = 3,

    /// <summary>No payment of that tenant matches it; nothing was written.</summary>
    NotFound = 4,
}
