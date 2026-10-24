using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using TenantFlow.Inventory.Domain.Stock;

namespace TenantFlow.Inventory.Infrastructure.Persistence.Configurations;

/// <summary>
/// The stored names of <see cref="ReservationStatus"/>, used by the mapping and by the store's SQL alike. A stored value
/// nobody recognises fails loudly instead of becoming a default.
/// </summary>
internal static class ReservationStatusNames
{
    public const string Reserved = "reserved";
    public const string Released = "released";

    public static readonly ValueConverter<ReservationStatus, string> Conversion = new(status => ToName(status), name => Parse(name));

    private static string ToName(ReservationStatus status) => status switch
    {
        ReservationStatus.Reserved => Reserved,
        ReservationStatus.Released => Released,
        _ => throw new ArgumentOutOfRangeException(nameof(status), "The reservation status is not one the domain defines."),
    };

    private static ReservationStatus Parse(string name) => name switch
    {
        Reserved => ReservationStatus.Reserved,
        Released => ReservationStatus.Released,
        _ => throw new InvalidOperationException("A stored reservation status is not one the domain defines."),
    };
}
