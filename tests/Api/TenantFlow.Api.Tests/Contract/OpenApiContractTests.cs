using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using TenantFlow.Api.Extensions;

namespace TenantFlow.Api.Tests.Contract;

/// <summary>
/// SPEC-0003 criterion 18 (ADR-0019): the generated OpenAPI document is the reviewed contract. Any difference from the
/// committed snapshot fails, so a breaking change cannot slip through and an additive one is a deliberate snapshot update
/// (set <c>TENANTFLOW_UPDATE_OPENAPI_SNAPSHOT=1</c> and run this class once). Every M3 operation documents its Problem
/// Details answers.
/// </summary>
public sealed class OpenApiContractTests
{
    private const string UpdateVariable = "TENANTFLOW_UPDATE_OPENAPI_SNAPSHOT";
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    [Fact]
    public async Task The_generated_document_matches_the_committed_snapshot()
    {
        var generated = await GenerateAsync();
        var snapshotPath = Path.Combine(RepositoryRoot(), "docs", "api", "openapi-v1.json");
        var text = generated.ToJsonString(Indented).ReplaceLineEndings("\n") + "\n";

        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            await File.WriteAllTextAsync(snapshotPath, text, TestContext.Current.CancellationToken);
        }

        Assert.True(File.Exists(snapshotPath), $"No snapshot at {snapshotPath}. Set {UpdateVariable}=1 and run this test once.");
        var committed = (await File.ReadAllTextAsync(snapshotPath, TestContext.Current.CancellationToken)).ReplaceLineEndings("\n");
        Assert.True(committed == text, $"The API contract changed. Review the difference, then set {UpdateVariable}=1 and rerun to accept it.");
    }

    [Fact]
    public async Task Every_catalog_and_inventory_operation_documents_its_problem_responses()
    {
        var paths = (await GenerateAsync())["paths"]!.AsObject()
            .Where(static path => path.Key.StartsWith("/v1/tenants/{tenantId}/products", StringComparison.Ordinal))
            .ToList();
        var missing = new List<string>();

        Assert.Equal(4, paths.Count);
        foreach (var (route, item) in paths)
        {
            foreach (var (method, operation) in item!.AsObject())
            {
                var responses = operation!["responses"]!.AsObject();
                // Every tenant route can deny at the boundary; a write can also reject its input.
                var expected = method is "post" or "put" ? new[] { "400", "401", "403", "404" } : ["401", "403", "404"];
                missing.AddRange(expected
                    .Where(status => responses[status]?["content"]?["application/problem+json"] is null)
                    .Select(status => $"{method.ToUpperInvariant()} {route}: {status}"));
            }
        }

        Assert.Empty(missing);
    }

    [Fact]
    public async Task Every_operation_is_grouped_under_a_feature_tag()
    {
        var untagged = new List<string>();
        foreach (var (route, item) in (await GenerateAsync())["paths"]!.AsObject())
        {
            foreach (var (method, operation) in item!.AsObject())
            {
                // Without WithTags a minimal API endpoint falls back to the assembly name, one flat group in the reference.
                var tags = operation!["tags"]?.AsArray().Select(static tag => tag!.GetValue<string>()).ToList() ?? [];
                if (tags.Count == 0 || tags.Contains("TenantFlow.Api"))
                {
                    untagged.Add($"{method.ToUpperInvariant()} {route}");
                }
            }
        }

        Assert.Empty(untagged);
    }

    private static async Task<JsonNode> GenerateAsync()
    {
        await using var factory = ApiFactory.CreateProduction(settings: new Dictionary<string, string> { [OpenApiExtensions.EnabledKey] = "true" });
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(new Uri(OpenApiExtensions.DocumentPath, UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TenantFlow.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("TenantFlow.sln was not found above the test output folder.");
    }
}
