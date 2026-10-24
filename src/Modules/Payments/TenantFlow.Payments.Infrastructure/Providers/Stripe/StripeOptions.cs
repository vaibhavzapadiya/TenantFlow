namespace TenantFlow.Payments.Infrastructure.Providers.Stripe;

/// <summary>
/// The Stripe test-mode adapter's settings (section <c>Payments:Stripe</c>, ADR-0022 decision 1). Both secrets come from
/// user-secrets or the environment, never a committed file (ADR-0008). Only a test key (<c>sk_test_</c>) is accepted: the
/// host refuses to start with any other while Stripe is the provider.
/// </summary>
public sealed class StripeOptions
{
    public const string SectionName = "Payments:Stripe";

    /// <summary>The prefix of every Stripe test-mode secret key.</summary>
    public const string TestKeyPrefix = "sk_test_";

    public string? SecretKey { get; set; }

    /// <summary>The endpoint's signing secret (<c>whsec_...</c>); without it every Stripe webhook is refused.</summary>
    public string? WebhookSecret { get; set; }

    public Uri BaseAddress { get; set; } = new("https://api.stripe.com/");

    /// <summary>A call that takes longer is an unknown outcome, reconciled later (never retried here).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);
}
