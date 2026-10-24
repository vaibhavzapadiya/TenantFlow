using System.Net.Security;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace TenantFlow.BuildingBlocks.Messaging.RabbitMq;

/// <summary>
/// The process's one broker connection, opened on first use. After that the client's automatic recovery owns it: a
/// connection that is recovering reports <c>IsOpen</c> false and must never be replaced, or the channels and consumers
/// opened on it would die with it instead of being recovered. Channels are created from it per purpose.
/// </summary>
public sealed class RabbitMqConnection(IOptions<RabbitMqOptions> options) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;

    public RabbitMqOptions Options { get; } = options.Value;

    /// <summary>The connection, possibly still recovering; opens it if no attempt has succeeded yet.</summary>
    public async Task<IConnection> GetAsync(CancellationToken cancellationToken)
    {
        if (_connection is { } existing)
        {
            return existing;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_connection is { } current)
            {
                return current;
            }

            var factory = new ConnectionFactory
            {
                HostName = Options.Host!,
                Port = Options.Port,
                UserName = Options.Username!,
                Password = Options.Password!,
                VirtualHost = Options.VirtualHost,
                ClientProvidedName = Options.ClientName,
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true,
                RequestedConnectionTimeout = TimeSpan.FromSeconds(5),
                // No accepted policy errors: an untrusted, expired or wrong-name certificate fails the connection.
                Ssl = new SslOption(Options.Host!) { Enabled = Options.UseTls, AcceptablePolicyErrors = SslPolicyErrors.None },
            };
            // A first connection that fails throws and leaves nothing behind; the next call tries again.
            _connection = await factory.CreateConnectionAsync(cancellationToken);
            return _connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        _gate.Dispose();
    }
}
