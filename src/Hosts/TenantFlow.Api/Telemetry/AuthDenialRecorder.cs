using System.Diagnostics.Metrics;
using TenantFlow.Api.Extensions;

namespace TenantFlow.Api.Telemetry;

/// <summary>
/// Counts and logs every authentication and authorization denial (SPEC-0002 criterion 23). The counter
/// <c>tenantflow.auth.denials</c> carries exactly two labels, <c>scheme</c> and <c>reason</c>, both from the fixed
/// lists below: never a tenant, a subject, a token, or any text a caller sent. The log line carries the same two values
/// and nothing else, so neither the token nor the <c>Authorization</c> header can reach a log.
/// </summary>
internal sealed partial class AuthDenialRecorder
{
    public const string MeterName = "TenantFlow.Api";
    public const string CounterName = "tenantflow.auth.denials";

    // Scheme labels.
    public const string TenantLabel = "tenant";
    public const string PlatformLabel = "platform";

    // Reason labels. A closed list: adding one is a change to the telemetry contract.
    public const string NoToken = "no_token";
    public const string InvalidToken = "invalid_token";
    public const string NotOperator = "not_operator";
    public const string NotAMember = "not_a_member";
    public const string TenantSuspended = "tenant_suspended";
    public const string PermissionDenied = "permission_denied";

    private readonly Counter<long> _denials;
    private readonly ILogger<AuthDenialRecorder> _logger;

    public AuthDenialRecorder(IMeterFactory meterFactory, ILogger<AuthDenialRecorder> logger)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        _logger = logger;
        _denials = meterFactory.Create(MeterName).CreateCounter<long>(
            CounterName, unit: "{denial}", description: "Requests denied by authentication or authorization, by scheme and reason.");
    }

    /// <summary>The label for an authentication scheme name (the scheme names are not themselves label values).</summary>
    public static string LabelOf(string? scheme) =>
        string.Equals(scheme, AuthenticationExtensions.PlatformScheme, StringComparison.Ordinal) ? PlatformLabel : TenantLabel;

    public void Record(string schemeLabel, string reason)
    {
        _denials.Add(1, new KeyValuePair<string, object?>("scheme", schemeLabel), new KeyValuePair<string, object?>("reason", reason));
        LogDenied(_logger, schemeLabel, reason);
    }

    [LoggerMessage(EventId = 2001, Level = LogLevel.Warning, Message = "Access denied: scheme {Scheme}, reason {Reason}")]
    private static partial void LogDenied(ILogger logger, string scheme, string reason);
}
