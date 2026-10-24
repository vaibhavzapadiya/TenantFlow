namespace TenantFlow.BuildingBlocks.Abstractions.Messaging;

/// <summary>What a consumer did with a message. Both outcomes are acknowledged.</summary>
public enum MessageOutcome
{
    /// <summary>The business effect and the processed-message record were committed together.</summary>
    Processed = 1,

    /// <summary>The message id was already processed by this consumer: nothing was written.</summary>
    Duplicate = 2,
}
