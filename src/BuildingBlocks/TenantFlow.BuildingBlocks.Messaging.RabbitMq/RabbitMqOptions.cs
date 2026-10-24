using System.ComponentModel.DataAnnotations;
using System.Net;

namespace TenantFlow.BuildingBlocks.Messaging.RabbitMq;

/// <summary>
/// Broker connection settings, bound from <c>Messaging:RabbitMq</c> (ADR-0008, ADR-0021). The password comes from
/// user-secrets locally and from the environment or a secret store elsewhere; it is never committed.
/// </summary>
public sealed class RabbitMqOptions : IValidatableObject
{
    public const string SectionName = "Messaging:RabbitMq";

    // Messages name the key, never the value, so a startup failure cannot leak a credential.
    [Required(ErrorMessage = "Messaging:RabbitMq:Host is required. In Development it comes from appsettings.Development.json or user-secrets.")]
    public string? Host { get; set; }

    [Range(1, 65535, ErrorMessage = "Messaging:RabbitMq:Port must be between 1 and 65535.")]
    public int Port { get; set; } = 5672;

    [Required(ErrorMessage = "Messaging:RabbitMq:Username is required. In Development it comes from appsettings.Development.json or user-secrets.")]
    public string? Username { get; set; }

    [Required(ErrorMessage = "Messaging:RabbitMq:Password is required. In Development it comes from appsettings.Development.json or user-secrets; elsewhere set it through the environment or a secret store.")]
    public string? Password { get; set; }

    public string VirtualHost { get; set; } = "/";

    /// <summary>The topic exchange integration events are published to; the routing key is the event type.</summary>
    [Required(ErrorMessage = "Messaging:RabbitMq:Exchange is required.")]
    public string Exchange { get; set; } = "tenantflow.events";

    /// <summary>The connection name the broker shows, for example <c>tenantflow-worker</c>.</summary>
    public string ClientName { get; set; } = "tenantflow";

    /// <summary>
    /// AMQPS with the broker's certificate validated against the host name (MW-A19). Off only for the local Compose
    /// broker; a deployed broker (Amazon MQ, port 5671) requires it.
    /// </summary>
    public bool UseTls { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        // Credentials and every event payload would cross the network in clear text, so plain AMQP is allowed only
        // to this machine.
        if (!UseTls && !string.IsNullOrWhiteSpace(Host) && !IsLoopback(Host))
        {
            yield return new ValidationResult(
                "Messaging:RabbitMq:UseTls must be true unless Messaging:RabbitMq:Host is a loopback address (local development).",
                [nameof(UseTls)]);
        }
    }

    private static bool IsLoopback(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));
}
