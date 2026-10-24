using System.Text.Json;
using System.Text.Json.Nodes;

namespace TenantFlow.Architecture.Tests;

/// <summary>
/// SPEC-0014 criteria 4 and 5: each local realm has one browser client for the API reference's sign-in (public,
/// authorization code with PKCE S256 only, redirects and origins only on the API's local address) that issues exactly
/// the claims of the realm's script client, and the script client stays password-grant only. Checked on every build.
/// </summary>
public sealed class RealmClientTests
{
    private const string ApiOrigin = "http://localhost:5080";
    private static readonly string Root = FindRoot();

    [Theory]
    [InlineData("tenantflow", "tenantflow-api-docs", "tenantflow-dev-cli")]
    [InlineData("tenantflow-platform", "tenantflow-platform-api-docs", "tenantflow-platform-dev-cli")]
    public void The_api_reference_client_signs_in_with_pkce_only_on_the_local_api_address(string realm, string docsClient, string devClient)
    {
        var clients = Clients(realm);
        var docs = Assert.Single(clients, client => (string?)client["clientId"] == docsClient);
        var dev = Assert.Single(clients, client => (string?)client["clientId"] == devClient);

        Assert.True((bool?)docs["enabled"]);
        Assert.True((bool?)docs["publicClient"]);
        Assert.True((bool?)docs["standardFlowEnabled"]);
        Assert.False((bool?)docs["directAccessGrantsEnabled"]);
        Assert.False((bool?)docs["implicitFlowEnabled"]);
        Assert.False((bool?)docs["serviceAccountsEnabled"]);
        Assert.Equal("S256", (string?)docs["attributes"]?["pkce.code.challenge.method"]);
        Assert.Equal([$"{ApiOrigin}/scalar/*"], Strings(docs["redirectUris"]));
        Assert.Equal([ApiOrigin], Strings(docs["webOrigins"]));
        // The same token as the script client: same audience, same roles claim, same scope rule.
        Assert.True(JsonNode.DeepEquals(dev["protocolMappers"], docs["protocolMappers"]), $"{docsClient} must issue the claims of {devClient}.");
        Assert.Equal((bool?)dev["fullScopeAllowed"], (bool?)docs["fullScopeAllowed"]);
        Assert.True(JsonNode.DeepEquals(dev["defaultClientScopes"], docs["defaultClientScopes"]));
    }

    [Theory]
    [InlineData("tenantflow", "tenantflow-dev-cli")]
    [InlineData("tenantflow-platform", "tenantflow-platform-dev-cli")]
    public void The_script_client_stays_password_grant_only_without_browser_access(string realm, string devClient)
    {
        var dev = Assert.Single(Clients(realm), client => (string?)client["clientId"] == devClient);

        Assert.True((bool?)dev["directAccessGrantsEnabled"]);
        Assert.False((bool?)dev["standardFlowEnabled"]);
        Assert.False((bool?)dev["implicitFlowEnabled"]);
        Assert.Empty(Strings(dev["redirectUris"]));
        Assert.Empty(Strings(dev["webOrigins"]));
    }

    private static List<JsonNode> Clients(string realm)
    {
        var text = File.ReadAllText(Path.Combine(Root, "deploy", "identity", "realms", "local", $"{realm}-realm.json"));
        var document = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip })!;
        return [.. document["clients"]!.AsArray().Select(static client => client!)];
    }

    private static List<string> Strings(JsonNode? array) =>
        array is null ? [] : [.. array.AsArray().Select(static item => item!.GetValue<string>())];

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TenantFlow.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("TenantFlow.sln was not found above the test output folder.");
    }
}
