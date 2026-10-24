using System.Collections.Frozen;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace TenantFlow.BuildingBlocks.Observability;

/// <summary>
/// Redacts exported log records (ADR-0031, SPEC-0010 criterion 7, threat model 0008 DP-A03). Processors run in the order
/// they are added, and <see cref="TelemetrySetup"/> adds this one before the OTLP exporter, so it runs first:
/// <list type="bullet">
/// <item>An exception becomes the <c>exception.type</c>, <c>exception.message</c> and <c>exception.stacktrace</c>
/// attributes. Type and stack are kept; the message is kept only for exceptions defined in TenantFlow assemblies, whose
/// messages are reviewed. A third-party message (a database driver, a provider SDK, a parser) can quote input values,
/// so it becomes <see cref="Redacted"/>, in the stack text of every inner exception too.</item>
/// <item>Scopes: OpenTelemetry's own scope export is off (<see cref="TelemetrySetup"/>), and only the keys in
/// <see cref="AllowedScopeKeys"/> are copied onto the record. A new scope key is added here deliberately, like a metric
/// label.</item>
/// </list>
/// The JSON console log is a separate provider and is not changed: it stays a local developer aid.
/// </summary>
internal sealed class LogRedactionProcessor : BaseProcessor<LogRecord>
{
    public const string Redacted = "[redacted]";

    /// <summary>Scope keys exported to the log backend. Everything else in a scope stays in the process.</summary>
    public static readonly FrozenSet<string> AllowedScopeKeys = FrozenSet.Create(
        StringComparer.Ordinal,
        // ASP.NET Core hosting and activity tracking.
        "RequestId", "RequestPath", "ConnectionId", "TraceId", "SpanId", "ParentId",
        // TenantFlow workers.
        "MessageId", "Queue", "Module", "Job");

    private readonly ExternalScopeCapture _scopes;

    public LogRedactionProcessor(ExternalScopeCapture scopes) => _scopes = scopes;

    public override void OnEnd(LogRecord data)
    {
        var attributes = new List<KeyValuePair<string, object?>>(data.Attributes ?? []);
        _scopes.Provider?.ForEachScope(AddAllowedScopeValues, attributes);

        if (data.Exception is { } exception)
        {
            attributes.Add(new("exception.type", exception.GetType().FullName));
            attributes.Add(new("exception.message", MessageOf(exception)));
            attributes.Add(new("exception.stacktrace", Describe(exception)));
            data.Exception = null;
        }

        data.Attributes = attributes;
    }

    /// <summary>The message of <paramref name="exception"/> if a TenantFlow assembly defines its type, otherwise <see cref="Redacted"/>.</summary>
    internal static string MessageOf(Exception exception) =>
        exception.GetType().Assembly.GetName().Name?.StartsWith("TenantFlow.", StringComparison.Ordinal) == true
            ? exception.Message
            : Redacted;

    /// <summary>The shape of <see cref="Exception.ToString"/> (type, message, inner exceptions, stack) with redacted messages.</summary>
    internal static string Describe(Exception exception)
    {
        var text = new StringBuilder();
        text.Append(exception.GetType().FullName).Append(": ").Append(MessageOf(exception));
        if (exception.InnerException is { } inner)
        {
            text.Append(" ---> ").Append(Describe(inner)).AppendLine().Append("   --- End of inner exception stack trace ---");
        }

        if (exception.StackTrace is { } stackTrace)
        {
            text.AppendLine().Append(stackTrace);
        }

        return text.ToString();
    }

    private static void AddAllowedScopeValues(object? scope, List<KeyValuePair<string, object?>> attributes)
    {
        if (scope is not IEnumerable<KeyValuePair<string, object?>> values)
        {
            return;
        }

        foreach (var (key, value) in values)
        {
            if (AllowedScopeKeys.Contains(key))
            {
                attributes.Add(new(key, value));
            }
        }
    }
}

/// <summary>
/// Gives <see cref="LogRedactionProcessor"/> the logging scopes of the current call. The logger factory hands its scope
/// provider to every provider that supports external scopes; this one writes nothing.
/// </summary>
internal sealed class ExternalScopeCapture : ILoggerProvider, ISupportExternalScope
{
    public IExternalScopeProvider? Provider { get; private set; }

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => Provider = scopeProvider;

    public ILogger CreateLogger(string categoryName) => NullLogger.Instance;

    public void Dispose()
    {
    }
}
