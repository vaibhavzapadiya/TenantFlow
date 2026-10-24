using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TenantFlow.BuildingBlocks.Abstractions.Errors;
using TenantFlow.Payments.Application.Abstractions;
using TenantFlow.Payments.Domain.Payments;

namespace TenantFlow.Payments.Application.Idempotency;

/// <summary>
/// The idempotency-key rules shared by payments and refunds (ADR-0022 decision 4). Each use case builds its own canonical
/// request; the caller's membership must be part of it, so another member who sends the same key gets 422, never someone
/// else's stored response.
/// </summary>
internal static class IdempotentRequests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>SHA-256 of the canonical request: what the key promises to mean.</summary>
    public static string Hash(string canonical) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

    /// <summary>
    /// Null for a new key. A finished request with the same body replays its response; a different body is refused
    /// (<see cref="PaymentErrors.IdempotencyKeyReused"/>); an unfinished one is <see cref="PaymentErrors.RequestInProgress"/>.
    /// </summary>
    public static async Task<T?> AnswerAsync<T>(IPaymentStore store, IdempotencyKeyRef key, string hash, CancellationToken cancellationToken)
        where T : class
    {
        var stored = await store.FindRequestAsync(key.Operation, key.Key, cancellationToken);
        if (stored is null)
        {
            return null;
        }

        if (!string.Equals(stored.RequestHash, hash, StringComparison.Ordinal))
        {
            throw new AppException(PaymentErrors.IdempotencyKeyReused);
        }

        return stored.ResponseJson is { } json
            ? JsonSerializer.Deserialize<T>(json, Json)
            : throw new AppException(PaymentErrors.RequestInProgress);
    }
}
