using TenantFlow.BuildingBlocks.Messaging.RabbitMq;

namespace TenantFlow.Database.Tests.Support;

/// <summary>
/// The local RabbitMQ for messaging tests (ADR-0021): the Compose stack's (<see cref="LocalStack"/>, ADR-0037), unless
/// environment variables name another broker. Each test publishes to an exchange of its own, so tests never see each
/// other's messages.
/// </summary>
internal static class BrokerTestSettings
{
    public const string PortVariable = "TENANTFLOW_TEST_RABBITMQ_PORT";
    public const string ManagementPortVariable = "TENANTFLOW_TEST_RABBITMQ_MANAGEMENT_PORT";
    public const string UserVariable = "TENANTFLOW_TEST_RABBITMQ_USER";
    public const string PasswordVariable = "TENANTFLOW_TEST_RABBITMQ_PASSWORD";

    /// <summary>The management API port, for tests that make the broker close a connection.</summary>
    public static int ManagementPort => int.Parse(Read(ManagementPortVariable), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Options for a fresh exchange named after the test, on the local broker, with a connection name of its own.</summary>
    public static RabbitMqOptions ForNewExchange()
    {
        var id = Guid.NewGuid().ToString("N");
        return new()
        {
            Host = "127.0.0.1",
            Port = int.Parse(Read(PortVariable), System.Globalization.CultureInfo.InvariantCulture),
            Username = Read(UserVariable),
            Password = Read(PasswordVariable),
            Exchange = $"tenantflow.events.test.{id}",
            ClientName = $"tenantflow-database-tests-{id}",
        };
    }

    /// <summary>The same exchange on a closed port: a broker that is down.</summary>
    public static RabbitMqOptions Unreachable(RabbitMqOptions options) => new()
    {
        Host = options.Host,
        Port = 1,
        Username = options.Username,
        Password = options.Password,
        Exchange = options.Exchange,
        ClientName = options.ClientName,
    };

    private static string Read(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : name switch
            {
                PortVariable => LocalStack.RabbitMqPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ManagementPortVariable => LocalStack.RabbitMqManagementPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
                UserVariable => LocalStack.RabbitMqUser,
                _ => LocalStack.RabbitMqPassword,
            };
}
