using System.Diagnostics.Metrics;

namespace TenantFlow.Operations.Application.Observability;

/// <summary>
/// Support-grant metrics (SPEC-0010 criterion 4, threat models 0001 and 0006): every support grant opened, without a
/// label. Who opened it, for which tenant and ticket, is in the audit log only.
/// </summary>
public sealed class OperationsMetrics : IDisposable
{
    public const string MeterName = "TenantFlow.Operations";
    public const string SupportGrantsCounter = "tenantflow.platform.support_grants";

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _grants;

    public OperationsMetrics() =>
        _grants = _meter.CreateCounter<long>(SupportGrantsCounter, "{grant}", "Support grants opened.");

    public void RecordGrantOpened() => _grants.Add(1);

    public void Dispose() => _meter.Dispose();
}
