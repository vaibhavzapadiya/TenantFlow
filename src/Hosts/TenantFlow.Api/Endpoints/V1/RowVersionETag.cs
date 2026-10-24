using TenantFlow.BuildingBlocks.Abstractions.Errors;

namespace TenantFlow.Api.Endpoints.V1;

/// <summary>
/// The ETag of a resource whose version is its PostgreSQL row version, in quotes; <c>If-Match</c> carries it back on an
/// edit (RFC 9110). Each resource names the error a missing header is reported with.
/// </summary>
internal static class RowVersionETag
{
    public static string Format(uint version) => $"\"{version}\"";

    /// <summary>
    /// Reads <c>If-Match</c>: <c>*</c> matches any current version (returns null); otherwise the versions of the listed
    /// strong tags. A weak tag (<c>W/"5"</c>) or a tag that is not ours never matches under the strong comparison
    /// <c>If-Match</c> requires, so it adds nothing; a list of only such tags therefore fails the precondition (412).
    /// </summary>
    /// <exception cref="AppException"><paramref name="missing"/> when the header is missing or empty.</exception>
    public static IReadOnlyCollection<uint>? Parse(string? ifMatch, AppError missing)
    {
        if (string.IsNullOrWhiteSpace(ifMatch))
        {
            throw new AppException(missing);
        }

        var tags = ifMatch.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tags is ["*"])
        {
            return null;
        }

        return [.. tags
            .Where(static tag => !tag.StartsWith("W/", StringComparison.Ordinal))
            .Select(static tag => uint.TryParse(tag.Trim('"'), out var version) ? version : (uint?)null)
            .OfType<uint>()];
    }
}
