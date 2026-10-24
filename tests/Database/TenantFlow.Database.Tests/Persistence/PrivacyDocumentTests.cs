namespace TenantFlow.Database.Tests.Persistence;

/// <summary>
/// SPEC-0009 criterion 7 (ADR-0025 decision 2): every table of every module schema has a classification, a purpose and a
/// retention rule in <c>docs/security/privacy-and-retention.md</c>, and the document names no table that does not exist.
/// A new table without a row fails here.
/// </summary>
public sealed class PrivacyDocumentTests(TenantDatabaseFixture fixture) : IClassFixture<TenantDatabaseFixture>
{
    private static readonly string[] Classes = ["public-product", "tenant-confidential", "personal-pseudonymous", "security-evidence"];

    // The module schemas (ADR-0012). The scheduler schema holds Quartz's own state and is described in the document's notes.
    private static readonly string[] ModuleSchemas =
        ["tenants", "identity_access", "catalog", "inventory", "orders", "notifications", "payments", "subscriptions", "imports_exports", "audit", "operations"];

    [Fact]
    public async Task Every_table_in_every_module_schema_has_a_class_a_purpose_and_a_retention_rule()
    {
        var tables = await MigratorRun.QueryAsync<string>(
            fixture.Database.OwnerConnectionString,
            """
            SELECT (table_schema || '.' || table_name)::text FROM information_schema.tables
            WHERE table_type = 'BASE TABLE' AND table_schema = ANY(@schemas) AND table_name <> '__ef_migrations_history'
            ORDER BY 1
            """,
            ("schemas", ModuleSchemas));
        var rows = await DocumentRowsAsync();

        var missing = tables.Except(rows.Keys).ToList();
        var unknown = rows.Keys.Except(tables).ToList();
        Assert.True(missing.Count == 0, $"Missing from docs/security/privacy-and-retention.md: {string.Join(", ", missing)}");
        Assert.True(unknown.Count == 0, $"Not a table: {string.Join(", ", unknown)}");
        Assert.All(rows, row =>
        {
            Assert.True(Classes.Contains(row.Value.Class), $"{row.Key}: unknown class '{row.Value.Class}'");
            Assert.False(string.IsNullOrWhiteSpace(row.Value.Purpose), $"{row.Key}: no purpose");
            Assert.False(string.IsNullOrWhiteSpace(row.Value.Retention), $"{row.Key}: no retention rule");
        });
    }

    // Table rows of the document: | `schema.table` | class | purpose | retention |
    private static async Task<Dictionary<string, (string Class, string Purpose, string Retention)>> DocumentRowsAsync()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TenantFlow.sln")))
        {
            directory = directory.Parent;
        }

        var path = Path.Combine(directory?.FullName ?? throw new InvalidOperationException("The repository root was not found."), "docs", "security", "privacy-and-retention.md");
        var rows = new Dictionary<string, (string, string, string)>(StringComparer.Ordinal);
        foreach (var line in await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken))
        {
            var cells = line.Split('|');
            if (cells.Length == 6 && cells[1].Trim() is ['`', .. var name, '`'] && name.Contains('.', StringComparison.Ordinal))
            {
                Assert.True(rows.TryAdd(name, (cells[2].Trim(), cells[3].Trim(), cells[4].Trim())), $"{name} is listed twice");
            }
        }

        return rows;
    }
}
