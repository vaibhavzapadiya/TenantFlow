using System.Security.Cryptography;
using System.Text;

namespace TenantFlow.Architecture.Tests;

/// <summary>
/// AGENTS.md rule 7 and SC-A12: an applied migration is never edited, renamed or deleted; a schema change is a new
/// migration (expand-contract). <c>applied-migrations.txt</c> holds every committed migration file with the SHA-256 of
/// its text, so an edit fails the build on every run, long before a release.
///
/// A new migration fails too until it is recorded: set <c>TENANTFLOW_RECORD_MIGRATIONS=1</c> and run this class once.
/// Recording only appends new files; a recorded file that changed or disappeared still fails. A migration that was
/// never released may be changed only by removing its line in the same reviewed change.
/// </summary>
public sealed class AppliedMigrationTests
{
    private const string RecordVariable = "TENANTFLOW_RECORD_MIGRATIONS";
    private static readonly string Root = FindRoot();
    private static readonly string ManifestPath = Path.Combine(Root, "tests", "Architecture", "TenantFlow.Architecture.Tests", "applied-migrations.txt");

    [Fact]
    public async Task Recorded_migrations_are_unchanged_and_every_migration_is_recorded()
    {
        var recorded = await ReadManifestAsync();
        var current = Migrations().ToDictionary(static path => path, Hash, StringComparer.Ordinal);

        var changed = recorded.Where(entry => !current.TryGetValue(entry.Key, out var hash) || hash != entry.Value)
            .Select(static entry => entry.Key)
            .ToList();
        var unrecorded = current.Keys.Where(path => !recorded.ContainsKey(path)).Order(StringComparer.Ordinal).ToList();

        if (unrecorded.Count > 0 && Environment.GetEnvironmentVariable(RecordVariable) == "1")
        {
            await File.AppendAllLinesAsync(ManifestPath, unrecorded.Select(path => $"{current[path]}  {path}"), TestContext.Current.CancellationToken);
            unrecorded.Clear();
        }

        Assert.True(changed.Count == 0, $"Applied migrations were edited, renamed or deleted; add a new migration instead:\n{string.Join('\n', changed)}");
        Assert.True(unrecorded.Count == 0, $"New migrations are not recorded; run this class once with {RecordVariable}=1:\n{string.Join('\n', unrecorded)}");
    }

    // Every migration and its designer file; the model snapshot changes with every new migration, so it is not recorded.
    private static IEnumerable<string> Migrations() =>
        Directory.GetFiles(Path.Combine(Root, "src"), "*.cs", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(Root, path).Replace('\\', '/'))
            .Where(static path => path.Contains("/Migrations/", StringComparison.Ordinal)
                && !path.Contains("/obj/", StringComparison.Ordinal) && !path.Contains("/bin/", StringComparison.Ordinal)
                && !path.EndsWith("ModelSnapshot.cs", StringComparison.Ordinal));

    // Line endings are normalised, so a checkout with CRLF hashes the same as the repository's LF.
    private static string Hash(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(File.ReadAllText(Path.Combine(Root, path)).ReplaceLineEndings("\n"))));

    private static async Task<Dictionary<string, string>> ReadManifestAsync()
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(ManifestPath))
        {
            return entries;
        }

        foreach (var line in await File.ReadAllLinesAsync(ManifestPath, TestContext.Current.CancellationToken))
        {
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split("  ", 2);
            entries[parts[1]] = parts[0];
        }

        return entries;
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TenantFlow.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
