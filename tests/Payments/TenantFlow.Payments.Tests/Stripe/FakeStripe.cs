using System.Net;
using System.Text;

namespace TenantFlow.Payments.Tests.Stripe;

/// <summary>An HTTP handler that answers with queued responses (or failures) and records every request it saw.</summary>
internal sealed class FakeStripe : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _answers = new();

    public List<(HttpMethod Method, string PathAndQuery, string? IdempotencyKey, string? Authorization, string Body)> Requests { get; } = [];

    public FakeStripe Answer(HttpStatusCode status, string body)
    {
        _answers.Enqueue(() => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        return this;
    }

    public FakeStripe Fail(Exception exception)
    {
        _answers.Enqueue(() => throw exception);
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.Method, request.RequestUri!.PathAndQuery, request.Headers.TryGetValues("Idempotency-Key", out var keys) ? keys.Single() : null,
            request.Headers.Authorization?.ToString(), body));
        return _answers.Dequeue()();
    }
}
