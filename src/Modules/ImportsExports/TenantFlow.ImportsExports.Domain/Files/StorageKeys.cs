using System.Globalization;
using System.Text.RegularExpressions;

namespace TenantFlow.ImportsExports.Domain.Files;

/// <summary>
/// Object storage keys (SPEC-0008 criterion 2, ADR-0024 decision 1): always made by the server as
/// <c>tenants/{tenantId}/{kind}/{fileId}</c> (ids as 32 hex digits), never from a client's file name, so a key cannot
/// leave the storage root and always names the tenant that owns it.
/// </summary>
public static partial class StorageKeys
{
    public static string For(Guid tenantId, string kind, Guid fileId)
    {
        if (tenantId == Guid.Empty || fileId == Guid.Empty || !FileKinds.All.Contains(kind))
        {
            throw new ArgumentException("A storage key needs a tenant, a known kind and a file id.");
        }

        return string.Create(CultureInfo.InvariantCulture, $"tenants/{tenantId:N}/{kind}/{fileId:N}");
    }

    /// <summary>True when <paramref name="key"/> has exactly the server's shape; anything else is refused.</summary>
    public static bool IsValid(string? key) => key is not null && KeyPattern().IsMatch(key);

    /// <summary>The tenant a valid key belongs to, or null for a key that is not valid.</summary>
    public static Guid? TenantOf(string? key) =>
        IsValid(key) && Guid.TryParseExact(key!.AsSpan(8, 32), "N", out var tenant) ? tenant : null;

    // \z, not $: $ also matches before a trailing newline.
    [GeneratedRegex("^tenants/[0-9a-f]{32}/(import_upload|export)/[0-9a-f]{32}\\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex KeyPattern();
}
