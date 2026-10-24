namespace TenantFlow.Payments.Infrastructure.Providers.Stripe;

/// <summary>
/// The HTTP client the Stripe adapter uses: one per process, pooled, with connections recycled so DNS changes are seen.
/// Tests build it on a fake handler instead of the network.
/// </summary>
internal sealed class StripeTransport(HttpClient client) : IDisposable
{
    public HttpClient Client { get; } = client;

    public static StripeTransport Create(StripeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        // The client owns the handler and disposes it.
        return new StripeTransport(new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }, disposeHandler: true)
        {
            BaseAddress = options.BaseAddress,
            Timeout = options.Timeout,
        });
    }

    public void Dispose() => Client.Dispose();
}
