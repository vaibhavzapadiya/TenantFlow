using System.Buffers.Text;
using System.Globalization;
using System.Text;
using TenantFlow.Audit.Application.Abstractions;
using TenantFlow.Audit.Application.Records.Dtos;
using TenantFlow.Audit.Domain.Records;
using TenantFlow.BuildingBlocks.Abstractions.Errors;

namespace TenantFlow.Audit.Application.Records.Services;

/// <summary>
/// The page cursor is opaque to clients: base64url of "utc ticks|id" of the last record on a page. It carries no tenant,
/// so it cannot widen what a caller sees. Every malformed cursor gets the same answer.
/// </summary>
internal static class AuditCursorCodec
{
    public static string Encode(AuditRecordDetails last) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{last.OccurredAt.UtcTicks}|{last.Id:D}")));

    /// <exception cref="AppException"><see cref="AuditErrors.CursorInvalid"/>.</exception>
    public static AuditCursor Decode(string cursor)
    {
        try
        {
            var parts = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor)).Split('|');
            if (parts.Length == 2
                && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                && ticks <= DateTimeOffset.MaxValue.UtcTicks
                && Guid.TryParseExact(parts[1], "D", out var id))
            {
                return new AuditCursor(new DateTimeOffset(ticks, TimeSpan.Zero), id);
            }
        }
        catch (FormatException)
        {
            // Falls through to the single answer for every malformed cursor.
        }

        throw new AppException(AuditErrors.CursorInvalid);
    }
}
