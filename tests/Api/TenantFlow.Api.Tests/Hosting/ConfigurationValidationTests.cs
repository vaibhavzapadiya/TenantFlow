namespace TenantFlow.Api.Tests.Hosting;

/// <summary>
/// SPEC-0001 criterion 9: a host with missing required configuration refuses to start, and the
/// error names the missing key without echoing any configuration value.
/// </summary>
public sealed class ConfigurationValidationTests
{
    private const string SentinelValue = "sentinel-value-that-must-never-appear-in-errors";

    [Fact]
    public async Task Host_refuses_to_start_without_the_connection_string_and_names_only_the_key()
    {
        await using var factory = ApiFactory.CreateProduction(
            connectionString: null,
            settings: new Dictionary<string, string> { ["Sentinel:Secret"] = SentinelValue });

        var exception = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(exception);
        var text = exception.ToString();
        Assert.Contains("ConnectionStrings:TenantFlow", text, StringComparison.Ordinal);
        Assert.DoesNotContain(SentinelValue, text, StringComparison.Ordinal);
    }

    // SPEC-0006 criterion 14 (ADR-0022 decision 1): Stripe runs in test mode only.
    [Fact]
    public async Task Host_refuses_to_start_with_a_stripe_key_that_is_not_a_test_key_and_names_only_the_key()
    {
        const string liveKey = "sk_live_" + SentinelValue;
        var refused = new List<string>();
        foreach (var key in new[] { liveKey, string.Empty })
        {
            await using var factory = ApiFactory.CreateProduction(settings: new Dictionary<string, string>
            {
                ["Payments:Provider"] = "stripe",
                ["Payments:Stripe:SecretKey"] = key,
            });
            refused.Add(Record.Exception(() => factory.CreateClient())?.ToString() ?? string.Empty);
        }

        await using var simulator = ApiFactory.CreateProduction(settings: new Dictionary<string, string> { ["Payments:Stripe:SecretKey"] = liveKey });
        var withSimulator = Record.Exception(() => simulator.CreateClient());

        Assert.All(refused, text => Assert.Contains("Payments:Stripe:SecretKey must be a Stripe test-mode key", text, StringComparison.Ordinal));
        Assert.All(refused, text => Assert.DoesNotContain(SentinelValue, text, StringComparison.Ordinal));
        Assert.Null(withSimulator);
    }

    // M6 review fix 4: the simulator charges nothing, so a host that would take payments with it must say so.
    [Fact]
    public async Task Host_refuses_to_take_payments_with_the_simulator_unless_it_is_switched_on()
    {
        await using var factory = ApiFactory.CreateProduction(settings: new Dictionary<string, string> { ["Payments:Simulator:Enabled"] = "false" });

        var exception = Record.Exception(() => factory.CreateClient());

        Assert.Contains("Payments:Simulator:Enabled must be true", exception?.ToString() ?? string.Empty, StringComparison.Ordinal);
    }

    // SPEC-0008 criterion 2 (ADR-0024): uploads and exports need a storage folder the API and the Worker share.
    [Fact]
    public async Task Host_refuses_to_start_without_a_storage_root_and_names_the_key()
    {
        await using var factory = ApiFactory.CreateProduction(settings: new Dictionary<string, string> { ["Storage:Root"] = string.Empty });

        var exception = Record.Exception(() => factory.CreateClient());

        Assert.Contains("Storage:Root is required", exception?.ToString() ?? string.Empty, StringComparison.Ordinal);
    }

    // SPEC-0012 criterion 16 (ADR-0024 amendment 1): the storage adapter is chosen explicitly, with the keys it needs.
    [Theory]
    [InlineData("", "", "", "Storage:Provider must be filesystem or s3")]
    [InlineData("s3-compatible", "", "", "Storage:Provider must be filesystem or s3")]
    [InlineData("s3", "", "ap-south-1", "Storage:S3:Bucket is required")]
    [InlineData("s3", "tenantflow-test-files", "", "Storage:S3:Region is required")]
    public async Task Host_refuses_to_start_with_incomplete_storage_settings_and_names_the_key(string provider, string bucket, string region, string message)
    {
        await using var factory = ApiFactory.CreateProduction(settings: new Dictionary<string, string>
        {
            ["Storage:Provider"] = provider,
            ["Storage:S3:Bucket"] = bucket,
            ["Storage:S3:Region"] = region,
            ["Sentinel:Secret"] = SentinelValue,
        });

        var text = Record.Exception(() => factory.CreateClient())?.ToString() ?? string.Empty;

        Assert.Contains(message, text, StringComparison.Ordinal);
        Assert.DoesNotContain(SentinelValue, text, StringComparison.Ordinal);
    }

    // SPEC-0012 criterion 16: deployed environments never keep files on a task's disk, so the filesystem must be chosen on purpose.
    [Fact]
    public async Task Host_refuses_filesystem_storage_unless_it_is_switched_on()
    {
        await using var factory = ApiFactory.CreateProduction(settings: new Dictionary<string, string> { ["Storage:Filesystem:Enabled"] = "false" });

        var exception = Record.Exception(() => factory.CreateClient());

        Assert.Contains("Storage:Filesystem:Enabled must be true", exception?.ToString() ?? string.Empty, StringComparison.Ordinal);
    }

    // SPEC-0012 criterion 16: S3 needs only a bucket and a region; credentials come from the task role, never configuration.
    [Fact]
    public async Task Host_starts_with_s3_storage_and_no_storage_credential()
    {
        await using var factory = ApiFactory.CreateProduction(settings: new Dictionary<string, string>
        {
            ["Storage:Provider"] = "s3",
            ["Storage:S3:Bucket"] = "tenantflow-test-files",
            ["Storage:S3:Region"] = "ap-south-1",
        });

        var exception = Record.Exception(() => factory.CreateClient());

        Assert.Null(exception);
    }

    // SPEC-0009 criterion 2 (ADR-0026 decision 3): without the key, platform actors could not be audited by reference.
    [Theory]
    [InlineData("")]
    [InlineData("not base64 " + SentinelValue)]
    [InlineData("c2hvcnQta2V5")]
    public async Task Host_refuses_to_start_without_a_usable_operator_reference_key_and_names_only_the_key(string key)
    {
        await using var factory = ApiFactory.CreateProduction(settings: new Dictionary<string, string> { ["Audit:OperatorReferenceKey"] = key });

        var text = Record.Exception(() => factory.CreateClient())?.ToString() ?? string.Empty;

        Assert.Contains("Audit:OperatorReferenceKey is required", text, StringComparison.Ordinal);
        Assert.DoesNotContain(SentinelValue, text, StringComparison.Ordinal);
    }
}
