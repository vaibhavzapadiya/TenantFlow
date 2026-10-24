using System.Buffers.Text;
using System.Text;
using TenantFlow.BuildingBlocks.Abstractions.Errors;
using TenantFlow.Catalog.Application.Abstractions;
using TenantFlow.Catalog.Application.Products.Dtos;
using TenantFlow.Catalog.Domain.Products;

namespace TenantFlow.Catalog.Application.Products.Services;

/// <summary>
/// The list cursor is opaque to clients: base64url of "id|sku" of the last product on a page. It carries no tenant,
/// so it cannot widen what a caller sees. Every malformed cursor gets the same answer.
/// </summary>
internal static class ProductCursorCodec
{
    public static string Encode(ProductDetails last) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{last.Id:D}|{last.Sku}"));

    /// <exception cref="AppException"><see cref="ProductErrors.CursorInvalid"/>.</exception>
    public static ProductCursor Decode(string cursor)
    {
        try
        {
            var text = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor));
            var separator = text.IndexOf('|', StringComparison.Ordinal);
            if (separator > 0 && Guid.TryParseExact(text[..separator], "D", out var id) && separator < text.Length - 1)
            {
                return new ProductCursor(text[(separator + 1)..], id);
            }
        }
        catch (FormatException)
        {
            // Falls through to the single answer for every malformed cursor.
        }

        throw new AppException(ProductErrors.CursorInvalid);
    }
}
