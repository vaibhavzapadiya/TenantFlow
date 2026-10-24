using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using TenantFlow.Tenants.Domain.Tenants;

namespace TenantFlow.Tenants.Infrastructure.Persistence.Configurations;

/// <summary>
/// Stores <see cref="TenantStatus"/> by its stable name, never as an enum number. The names and the strict parse
/// belong to the domain (<see cref="TenantStatusNames"/>): a stored value nobody recognises fails loudly.
/// </summary>
internal static class TenantStatusConversion
{
    public static readonly ValueConverter<TenantStatus, string> Required =
        new(status => status.ToName(), name => TenantStatusNames.Parse(name));

    public static readonly ValueConverter<TenantStatus?, string?> Optional =
        new(status => status == null ? null : status.Value.ToName(), name => name == null ? null : TenantStatusNames.Parse(name));
}
