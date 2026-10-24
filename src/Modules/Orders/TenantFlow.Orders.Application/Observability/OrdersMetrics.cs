using System.Diagnostics.Metrics;

namespace TenantFlow.Orders.Application.Observability;

/// <summary>
/// Order metrics (SPEC-0010 criterion 4): submitted orders whose stock reservation was refused, by the rejection reason
/// recorded on the order. Counted once per order, by the submit or recovery that recorded the rejection. The reason is a
/// fixed name (<c>insufficient_stock</c> today), never a product or tenant id.
/// </summary>
public sealed class OrdersMetrics : IDisposable
{
    public const string MeterName = "TenantFlow.Orders";
    public const string ReservationConflictsCounter = "tenantflow.orders.reservation_conflicts";
    public const string ReasonTag = "reason";

    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _conflicts;

    public OrdersMetrics() =>
        _conflicts = _meter.CreateCounter<long>(ReservationConflictsCounter, "{order}", "Orders rejected because stock could not be reserved, by reason.");

    public void RecordReservationConflict(string reason) => _conflicts.Add(1, new KeyValuePair<string, object?>(ReasonTag, reason));

    public void Dispose() => _meter.Dispose();
}
