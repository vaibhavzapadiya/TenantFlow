namespace TenantFlow.Payments.Tests.Stripe;

/// <summary>
/// Stripe API bodies in the shapes Stripe documents for PaymentIntents, errors, search results and events (API reference,
/// trimmed to the fields the adapter reads). Written from the documentation, not captured from a live account: a live
/// test-mode run is a manual, opt-in check (docs/operations/local-environment.md).
/// </summary>
internal static class StripeFixtures
{
    public static string Intent(string id, string status, string? declineCode = null) => $$"""
        {
          "id": "{{id}}",
          "object": "payment_intent",
          "amount": 1250,
          "currency": "eur",
          "status": "{{status}}",
          "last_payment_error": {{(declineCode is null ? "null" : $$"""{ "type": "card_error", "code": "card_declined", "decline_code": "{{declineCode}}" }""")}},
          "metadata": { "tenant_id": "00000000-0000-0000-0000-000000000001", "payment_id": "00000000-0000-0000-0000-000000000002" }
        }
        """;

    public static string Refund(string id, string status, Guid refundId, string? failureReason = null) => $$"""
        { "id": "{{id}}", "object": "refund", "status": "{{status}}", "amount": 400, "currency": "eur", "payment_intent": "pi_1",
          "failure_reason": {{(failureReason is null ? "null" : $"\"{failureReason}\"")}},
          "metadata": { "tenant_id": "00000000-0000-0000-0000-000000000000", "refund_id": "{{refundId}}" } }
        """;

    public static string CardDeclined(string intentId) => $$"""
        {
          "error": {
            "type": "card_error",
            "code": "card_declined",
            "decline_code": "insufficient_funds",
            "message": "Your card has insufficient funds.",
            "payment_intent": { "id": "{{intentId}}", "object": "payment_intent", "status": "requires_payment_method" }
          }
        }
        """;

    public const string RateLimited = """{ "error": { "type": "invalid_request_error", "code": "rate_limit", "message": "Too many requests." } }""";

    public static string Search(params string[] intents) => $$"""{ "object": "search_result", "data": [{{string.Join(",", intents)}}], "has_more": false }""";

    public static string Event(string eventId, string type, Guid tenantId, Guid paymentId, string intentId, string? code = null) => $$"""
        {
          "id": "{{eventId}}",
          "object": "event",
          "type": "{{type}}",
          "data": {
            "object": {
              "id": "{{intentId}}",
              "object": "payment_intent",
              "metadata": { "tenant_id": "{{tenantId}}", "payment_id": "{{paymentId}}" },
              "last_payment_error": {{(code is null ? "null" : $$"""{ "code": "{{code}}" }""")}}
            }
          }
        }
        """;
}
