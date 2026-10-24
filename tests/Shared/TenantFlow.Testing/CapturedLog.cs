using System.Diagnostics;

namespace TenantFlow.Testing;

public sealed record CapturedLog(
    string? Category,
    string? Message,
    ActivityTraceId TraceId,
    ActivitySpanId SpanId,
    IReadOnlyDictionary<string, object> Resource)
{
    /// <summary>Everything the record would export besides the message: attributes, scopes and the exception text.</summary>
    public IReadOnlyList<string> Exported { get; init; } = [];

    /// <summary>The record's attributes as exported (after every earlier processor in the pipeline ran).</summary>
    public IReadOnlyDictionary<string, string?> Attributes { get; init; } = new Dictionary<string, string?>();
}
