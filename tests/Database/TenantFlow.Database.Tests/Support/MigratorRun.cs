using Microsoft.Extensions.Logging;
using Npgsql;
using TenantFlow.Migrator;

namespace TenantFlow.Database.Tests.Support;

/// <summary>Runs the Migrator in-process, in Production, and captures what it logs.</summary>
internal static class MigratorRun
{
    public static async Task<(int ExitCode, IReadOnlyList<string> Logs)> RunAsync(
        string? ownerConnectionString,
        IReadOnlyDictionary<string, string>? settings = null)
    {
        // Always pass the key: an empty value stands for "missing", so an ambient
        // ConnectionStrings__TenantFlowOwner environment variable cannot satisfy a test by accident.
        var args = new List<string>
        {
            "--environment=Production",
            $"--ConnectionStrings:{MigratorApplication.ConnectionStringName}={ownerConnectionString}",
        };
        args.AddRange((settings ?? new Dictionary<string, string>()).Select(static s => $"--{s.Key}={s.Value}"));

        var capture = new CapturingLoggerProvider();
        var exitCode = await MigratorApplication.RunAsync(
            [.. args],
            logging => logging.AddProvider(capture),
            TestContext.Current.CancellationToken);
        return (exitCode, capture.Messages);
    }

    /// <summary>Runs a query as the given connection and returns the first column of every row.</summary>
    public static async Task<IReadOnlyList<T>> QueryAsync<T>(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var results = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            results.Add(reader.GetFieldValue<T>(0));
        }

        return results;
    }
}
