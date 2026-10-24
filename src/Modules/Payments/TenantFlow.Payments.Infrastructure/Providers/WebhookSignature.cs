using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TenantFlow.Payments.Application.Abstractions;

namespace TenantFlow.Payments.Infrastructure.Providers;

/// <summary>
/// Stripe's webhook signature scheme, used by the simulator too (ADR-0022 decision 5): a header <c>t=&lt;unix seconds&gt;,v1=&lt;hex&gt;</c>
/// (several <c>v1</c> during a secret rotation), where each <c>v1</c> is HMAC-SHA256 of <c>"{t}."</c> followed by the body's
/// exact bytes, with the shared secret (never a decoded and re-encoded copy, which a BOM or an invalid byte would change). Compared in constant time; a timestamp more than <see cref="Tolerance"/> away from now, either way, is refused,
/// so a captured request cannot be replayed later (and a duplicate inside the window is caught by its event id).
/// </summary>
public static class WebhookSignature
{
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    /// <summary>The header value for <paramref name="body"/> signed at <paramref name="signedAt"/>: what a provider sends.</summary>
    public static string Create(string secret, ReadOnlySpan<byte> body, DateTimeOffset signedAt)
    {
        var timestamp = signedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return $"t={timestamp},v1={Convert.ToHexStringLower(Mac(secret, timestamp, body))}";
    }

    /// <summary>As <see cref="Create(string, ReadOnlySpan{byte}, DateTimeOffset)"/>, for a body sent as UTF-8 text.</summary>
    public static string Create(string secret, string body, DateTimeOffset signedAt) => Create(secret, Encoding.UTF8.GetBytes(body), signedAt);

    /// <exception cref="WebhookRejectedException">No secret, a missing or unreadable header, no matching signature, or a timestamp outside the tolerance.</exception>
    public static void Verify(string? secret, string? header, ReadOnlySpan<byte> body, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(header))
        {
            // No secret configured fails closed: nothing is accepted.
            throw new WebhookRejectedException(WebhookRejection.Signature);
        }

        string? timestamp = null;
        var signatures = new List<byte[]>();
        foreach (var part in header.Split(','))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2)
            {
                continue;
            }

            if (pair[0] == "t")
            {
                timestamp = pair[1];
            }
            else if (pair[0] == "v1" && pair[1].Length == 64 && pair[1].All(Uri.IsHexDigit))
            {
                signatures.Add(Convert.FromHexString(pair[1]));
            }
        }

        if (timestamp is null || signatures.Count == 0 || !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            throw new WebhookRejectedException(WebhookRejection.Signature);
        }

        var expected = Mac(secret, timestamp, body);
        if (!signatures.Any(s => CryptographicOperations.FixedTimeEquals(s, expected)))
        {
            throw new WebhookRejectedException(WebhookRejection.Signature);
        }

        // Checked after the signature, so an unsigned request learns nothing about the clock.
        if (seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds() || (now - DateTimeOffset.FromUnixTimeSeconds(seconds)).Duration() > Tolerance)
        {
            throw new WebhookRejectedException(WebhookRejection.Timestamp);
        }
    }

    private static byte[] Mac(string secret, string timestamp, ReadOnlySpan<byte> body)
    {
        var prefix = Encoding.ASCII.GetBytes($"{timestamp}.");
        var signed = new byte[prefix.Length + body.Length];
        prefix.CopyTo(signed, 0);
        body.CopyTo(signed.AsSpan(prefix.Length));
        return HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signed);
    }
}
