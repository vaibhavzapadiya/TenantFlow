using System.Text.Json.Nodes;

namespace TenantFlow.Architecture.Tests;

/// <summary>
/// The local realms (deploy/identity/realms/local) and the production realms (deploy/identity/realms/production) are two
/// files each, so nothing else stops them drifting. The API trusts the same realm names, roles and token shape in both:
/// every client of a realm, local or production, issues the same audience and role claims and the same default scopes.
/// Clients, users and the production-only login policies (OTP, brute-force protection) may differ. Checked on every build.
/// </summary>
public sealed class RealmParityTests
{
    private static readonly string Root = FindRoot();

    [Theory]
    [InlineData("tenantflow")]
    [InlineData("tenantflow-platform")]
    public void Local_and_production_realms_issue_the_same_realm_roles_and_token_shape(string realm)
    {
        var local = Load("local", realm);
        var production = Load("production", realm);

        Assert.Equal((string?)production["realm"], (string?)local["realm"]);
        Assert.Equal(RealmRoles(production), RealmRoles(local));

        var shapes = Clients(local).Concat(Clients(production)).Select(TokenShape).Distinct().ToList();
        Assert.True(shapes.Count == 1, $"{realm}: clients issue different claims or scopes: {string.Join(" | ", shapes)}");
    }

    private static JsonNode Load(string variant, string realm) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "deploy", "identity", "realms", variant, $"{realm}-realm.json")))!;

    private static List<string> RealmRoles(JsonNode realm) =>
        [.. (realm["roles"]?["realm"]?.AsArray() ?? []).Select(static role => (string)role!["name"]!).Order(StringComparer.Ordinal)];

    private static IEnumerable<JsonNode> Clients(JsonNode realm) => realm["clients"]!.AsArray().Select(static client => client!);

    // What a client's tokens carry: its protocol mappers (type and the audience or claim they add) and its default scopes.
    private static string TokenShape(JsonNode client)
    {
        var mappers = (client["protocolMappers"]?.AsArray() ?? []).Select(static mapper =>
            $"{(string?)mapper!["protocolMapper"]}={(string?)mapper["config"]?["included.custom.audience"] ?? (string?)mapper["config"]?["claim.name"]}");
        var scopes = (client["defaultClientScopes"]?.AsArray() ?? []).Select(static scope => (string)scope!);
        return $"mappers[{string.Join(',', mappers.Order(StringComparer.Ordinal))}] scopes[{string.Join(',', scopes.Order(StringComparer.Ordinal))}]";
    }

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
