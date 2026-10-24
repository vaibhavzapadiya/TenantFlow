using System.Diagnostics;

namespace TenantFlow.Testing;

public sealed record CapturedSpan(
    string OperationName,
    string DisplayName,
    ActivityKind Kind,
    ActivityTraceId TraceId,
    ActivitySpanId SpanId,
    IReadOnlyDictionary<string, string?> Tags,
    IReadOnlyDictionary<string, object> Resource);
