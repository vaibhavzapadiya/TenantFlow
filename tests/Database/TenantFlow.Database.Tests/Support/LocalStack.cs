using Npgsql;

namespace TenantFlow.Database.Tests.Support;

/// <summary>
/// The connection settings of the running Compose stack (ADR-0037), from <see cref="ComposeSettings"/>. So
/// <c>dotnet run --project tests/Database/TenantFlow.Database.Tests</c> finds the stack with no script.
/// </summary>
internal static class LocalStack
{
    public static string AdminConnectionString =>
        Connection(ComposeSettings.Get("POSTGRES_USER"), ComposeSettings.Get("POSTGRES_PASSWORD"), database: "postgres");

    public static string OwnerConnectionString => Connection(TestDatabase.OwnerRole, ComposeSettings.Get("TENANTFLOW_OWNER_PASSWORD"), database: null);

    public static string AppConnectionString => Connection(TestDatabase.AppRole, ComposeSettings.Get("TENANTFLOW_APP_PASSWORD"), database: null);

    public static int RabbitMqPort => ComposeSettings.GetPort("RABBITMQ_PORT");

    public static int RabbitMqManagementPort => ComposeSettings.GetPort("RABBITMQ_MANAGEMENT_PORT");

    public static string RabbitMqUser => ComposeSettings.Get("RABBITMQ_USER");

    public static string RabbitMqPassword => ComposeSettings.Get("RABBITMQ_PASSWORD");

    private static string Connection(string user, string password, string? database) =>
        new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = ComposeSettings.GetPort("POSTGRES_PORT"),
            Database = database,
            Username = user,
            Password = password,
        }.ConnectionString;
}
