using System.Globalization;
using System.Text;
using RabbitMQ.Client;
using TenantFlow.BuildingBlocks.Abstractions.Messaging;

namespace TenantFlow.BuildingBlocks.Messaging.RabbitMq;

/// <summary>
/// Reads the ADR-0021 envelope from a delivered message, the reverse of <see cref="RabbitMqMessageBus"/>. A message without
/// a valid id, type, version or tenant can never be processed, so it is rejected as invalid rather than retried.
/// </summary>
public static class MessageEnvelope
{
    /// <exception cref="InvalidMessageException">The envelope is incomplete or malformed.</exception>
    public static IncomingMessage Read(IReadOnlyBasicProperties properties, ReadOnlyMemory<byte> body)
    {
        ArgumentNullException.ThrowIfNull(properties);
        if (!Guid.TryParse(properties.MessageId, out var messageId) || messageId == Guid.Empty)
        {
            throw new InvalidMessageException("Missing or malformed message id.");
        }

        if (string.IsNullOrWhiteSpace(properties.Type))
        {
            throw new InvalidMessageException("Missing message type.");
        }

        if (!Guid.TryParse(ReadHeader(properties, RabbitMqMessageBus.TenantIdHeader), out var tenantId) || tenantId == Guid.Empty)
        {
            throw new InvalidMessageException("Missing or malformed tenant id.");
        }

        if (!int.TryParse(ReadHeader(properties, RabbitMqMessageBus.VersionHeader), NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version < 1)
        {
            throw new InvalidMessageException("Missing or malformed message version.");
        }

        string payload;
        try
        {
            payload = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(body.Span);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidMessageException("Unreadable payload.", exception);
        }

        return new IncomingMessage(messageId, properties.Type, version, tenantId, payload, properties.CorrelationId, ReadHeader(properties, RabbitMqMessageBus.TraceParentHeader));
    }

    /// <summary>A string header, whether the client delivered it as UTF-8 bytes (the AMQP norm) or as a string.</summary>
    internal static string? ReadHeader(IReadOnlyBasicProperties properties, string name) =>
        properties.Headers is { } headers && headers.TryGetValue(name, out var value)
            ? value switch
            {
                byte[] bytes => Encoding.UTF8.GetString(bytes),
                string text => text,
                _ => null,
            }
            : null;
}
