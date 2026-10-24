namespace TenantFlow.Payments.Application.Abstractions;

/// <summary>
/// Every payment provider adapter the host has, and the one new payments go to (<c>Payments:Provider</c>). A payment is
/// always settled by the provider that took it (its stored <c>Provider</c>), whatever is configured for new payments now,
/// so switching providers, or an API and a Worker configured differently, never asks the wrong provider about a charge.
/// </summary>
public sealed class PaymentProviders
{
    private readonly Dictionary<string, IPaymentProvider> _byName;

    public PaymentProviders(IEnumerable<IPaymentProvider> providers, string current)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _byName = providers.ToDictionary(static p => p.Name, StringComparer.Ordinal);
        Current = _byName.TryGetValue(current, out var chosen)
            ? chosen
            : throw new ArgumentException("The configured payment provider is not registered.", nameof(current));
    }

    /// <summary>The provider that takes new payments.</summary>
    public IPaymentProvider Current { get; }

    public IEnumerable<IPaymentProvider> All => _byName.Values;

    /// <summary>The provider of that name, or null when the host has none (a payment of an adapter since removed).</summary>
    public IPaymentProvider? Find(string name) => _byName.GetValueOrDefault(name);
}
