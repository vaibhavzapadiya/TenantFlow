using TenantFlow.BuildingBlocks.Abstractions.Errors;
using TenantFlow.Payments.Application.Abstractions;
using TenantFlow.Payments.Domain.Payments;
using TenantFlow.Tenants.Contracts;

namespace TenantFlow.Payments.Application.Payments.Services;

/// <summary>
/// Receives provider webhooks (SPEC-0006 criteria 7 to 10, ADR-0022 decision 5). The route is anonymous: the signature is
/// the only authentication, so nothing is read or written before it verifies. The tenant comes from TenantFlow's own
/// metadata in the signed event; it selects the payment, and the store requires the stored provider reference to match,
/// so a valid signature for one payment can never act on another tenant's. Arrival order decides nothing: duplicates are
/// recognised by event id, and the state table ignores late or out-of-order news.
/// </summary>
public sealed class PaymentWebhookService(IEnumerable<IPaymentWebhookReader> readers, IPaymentStore store, ITenantDirectory tenants, TimeProvider clock)
{
    /// <summary>The header that carries <paramref name="provider"/>'s signature, or null for an unknown provider.</summary>
    public string? SignatureHeaderOf(string provider) => Find(provider)?.SignatureHeader;

    /// <exception cref="AppException">
    /// <see cref="PaymentErrors.WebhookProviderUnknown"/>, <see cref="PaymentErrors.WebhookSignatureInvalid"/>,
    /// <see cref="PaymentErrors.WebhookTimestampOutOfTolerance"/>, <see cref="PaymentErrors.WebhookMalformed"/> or
    /// <see cref="PaymentErrors.WebhookPaymentNotFound"/>.
    /// </exception>
    public async Task<WebhookOutcome> HandleAsync(string provider, string? signature, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        var reader = Find(provider) ?? throw new AppException(PaymentErrors.WebhookProviderUnknown);
        ProviderWebhookEvent? webhook;
        try
        {
            webhook = reader.Read(signature, body, clock.GetUtcNow());
        }
        catch (WebhookRejectedException rejected)
        {
            throw new AppException(rejected.Reason switch
            {
                WebhookRejection.Timestamp => PaymentErrors.WebhookTimestampOutOfTolerance,
                WebhookRejection.Malformed => PaymentErrors.WebhookMalformed,
                _ => PaymentErrors.WebhookSignatureInvalid,
            }, rejected);
        }

        if (webhook is null)
        {
            return WebhookOutcome.Ignored;
        }

        // A tenant that does not exist gets the same answer as a payment that does not: no oracle for either.
        var known = await tenants.GetAvailabilityAsync([webhook.TenantId], cancellationToken);
        var outcome = known.ContainsKey(webhook.TenantId)
            ? await store.ApplyWebhookAsync(webhook.TenantId, reader.Provider, webhook, cancellationToken)
            : WebhookOutcome.NotFound;
        return outcome == WebhookOutcome.NotFound ? throw new AppException(PaymentErrors.WebhookPaymentNotFound) : outcome;
    }

    private IPaymentWebhookReader? Find(string provider) =>
        readers.FirstOrDefault(r => string.Equals(r.Provider, provider, StringComparison.Ordinal));
}
