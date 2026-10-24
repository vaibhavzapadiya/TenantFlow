using System.Text.Json;

namespace TenantFlow.Architecture.Tests;

/// <summary>
/// SPEC-0012 criterion 14 (PS-A17, ADR-0035): the production platform realm makes every operator use an OTP before any
/// token is issued, and offers no way around the browser flow. The live behaviour was checked against Keycloak 26.8 in a
/// throwaway container (SPEC-0012 Verification); this test keeps the file from drifting.
/// </summary>
public sealed class ProductionRealmTests
{
    private static readonly string Root = FindRoot();

    private static JsonElement Realm { get; } = Load("tenantflow-platform-realm.json");

    private static JsonElement TenantRealm { get; } = Load("tenantflow-realm.json");

    // Tenant users (decided by the user, 2026-10-23): created by an operator with a temporary password, OTP optional but
    // asked at every sign-in once enrolled. Checked live against Keycloak 26.8 in S3g.
    [Fact]
    public void The_tenant_realm_asks_for_an_otp_exactly_when_the_user_has_one()
    {
        var flows = TenantRealm.GetProperty("authenticationFlows").EnumerateArray().ToDictionary(static f => f.GetProperty("alias").GetString()!);
        var browser = flows[TenantRealm.GetProperty("browserFlow").GetString()!];
        var forms = flows[browser.GetProperty("authenticationExecutions").EnumerateArray()
            .Single(static e => e.GetProperty("authenticatorFlow").GetBoolean()).GetProperty("flowAlias").GetString()!];

        var formSteps = forms.GetProperty("authenticationExecutions").EnumerateArray().ToList();
        Assert.Equal("REQUIRED", formSteps.Single(static e => e.TryGetProperty("authenticator", out var a) && a.GetString() == "auth-username-password-form").GetProperty("requirement").GetString());
        var conditional = formSteps.Single(static e => e.GetProperty("authenticatorFlow").GetBoolean());
        Assert.Equal("CONDITIONAL", conditional.GetProperty("requirement").GetString());

        var otpSteps = flows[conditional.GetProperty("flowAlias").GetString()!].GetProperty("authenticationExecutions").EnumerateArray()
            .ToDictionary(static e => e.GetProperty("authenticator").GetString()!, static e => e.GetProperty("requirement").GetString());
        Assert.Equal(new Dictionary<string, string?> { ["conditional-user-configured"] = "REQUIRED", ["auth-otp-form"] = "REQUIRED" }, otpSteps);
    }

    [Fact]
    public void Tenant_users_are_created_by_an_operator_and_no_tenant_client_skips_the_browser_flow()
    {
        Assert.False(TenantRealm.TryGetProperty("users", out _));
        Assert.Equal("all", TenantRealm.GetProperty("sslRequired").GetString());
        Assert.True(TenantRealm.GetProperty("bruteForceProtected").GetBoolean());
        Assert.False(TenantRealm.GetProperty("registrationAllowed").GetBoolean());
        Assert.False(TenantRealm.GetProperty("resetPasswordAllowed").GetBoolean());

        var actions = TenantRealm.GetProperty("requiredActions").EnumerateArray().ToDictionary(static a => a.GetProperty("alias").GetString()!);
        Assert.True(actions["UPDATE_PASSWORD"].GetProperty("enabled").GetBoolean());
        Assert.False(actions["CONFIGURE_TOTP"].GetProperty("defaultAction").GetBoolean());

        Assert.All(TenantRealm.GetProperty("clients").EnumerateArray(), static client =>
        {
            Assert.False(client.GetProperty("directAccessGrantsEnabled").GetBoolean());
            Assert.False(client.GetProperty("implicitFlowEnabled").GetBoolean());
            Assert.False(client.GetProperty("serviceAccountsEnabled").GetBoolean());
            Assert.Equal("S256", client.GetProperty("attributes").GetProperty("pkce.code.challenge.method").GetString());
        });
    }

    private static JsonElement Load(string file) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "deploy", "identity", "realms", "production", file))).RootElement;

    [Fact]
    public void The_browser_flow_requires_a_password_and_an_otp_on_every_path_to_a_token()
    {
        var flows = Realm.GetProperty("authenticationFlows").EnumerateArray().ToDictionary(static f => f.GetProperty("alias").GetString()!);
        var browser = flows[Realm.GetProperty("browserFlow").GetString()!];

        // The top-level flow offers only the session cookie (an earlier full sign-in) or the forms sub-flow.
        var alternatives = browser.GetProperty("authenticationExecutions").EnumerateArray().ToList();
        Assert.All(alternatives, static e => Assert.Equal("ALTERNATIVE", e.GetProperty("requirement").GetString()));
        Assert.Equal(["auth-cookie"], alternatives.Where(static e => !e.GetProperty("authenticatorFlow").GetBoolean()).Select(static e => e.GetProperty("authenticator").GetString()));
        var forms = flows[alternatives.Single(static e => e.GetProperty("authenticatorFlow").GetBoolean()).GetProperty("flowAlias").GetString()!];

        var steps = forms.GetProperty("authenticationExecutions").EnumerateArray()
            .ToDictionary(static e => e.GetProperty("authenticator").GetString()!, static e => e.GetProperty("requirement").GetString());
        Assert.Equal(new Dictionary<string, string?> { ["auth-username-password-form"] = "REQUIRED", ["auth-otp-form"] = "REQUIRED" }, steps);
    }

    [Fact]
    public void Every_operator_must_set_up_an_otp_and_no_client_can_skip_the_browser_flow()
    {
        var totp = Realm.GetProperty("requiredActions").EnumerateArray().Single(static a => a.GetProperty("alias").GetString() == "CONFIGURE_TOTP");
        Assert.True(totp.GetProperty("enabled").GetBoolean() && totp.GetProperty("defaultAction").GetBoolean());

        Assert.All(Realm.GetProperty("clients").EnumerateArray(), static client =>
        {
            var id = client.GetProperty("clientId").GetString();
            Assert.False(client.GetProperty("directAccessGrantsEnabled").GetBoolean(), $"{id}: the password grant skips OTP");
            Assert.False(client.GetProperty("implicitFlowEnabled").GetBoolean(), $"{id}: implicit flow");
            Assert.False(client.GetProperty("serviceAccountsEnabled").GetBoolean(), $"{id}: a service account is not an operator");
            Assert.Equal("S256", client.GetProperty("attributes").GetProperty("pkce.code.challenge.method").GetString());
        });
    }

    [Fact]
    public void The_realm_has_no_users_requires_tls_and_limits_guessing()
    {
        Assert.False(Realm.TryGetProperty("users", out _), "operators are created by an administrator, never imported");
        Assert.Equal("all", Realm.GetProperty("sslRequired").GetString());
        Assert.True(Realm.GetProperty("bruteForceProtected").GetBoolean());
        Assert.False(Realm.GetProperty("registrationAllowed").GetBoolean());
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
