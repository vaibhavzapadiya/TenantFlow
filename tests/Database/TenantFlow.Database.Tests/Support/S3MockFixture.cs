using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace TenantFlow.Database.Tests.Support;

/// <summary>
/// Adobe S3Mock for the S3 storage adapter (SPEC-0012 S3c, ADR-0024 amendment 1). These tests start it through the Docker
/// CLI on a loopback port of their own and remove it afterwards; it is never part of the Compose stack. The credentials
/// are placeholders, which S3Mock does not check.
/// </summary>
public sealed class S3MockFixture : IAsyncLifetime
{
    /// <summary>Test-only, pinned by digest: 5.2.3 (2026-09-19) is outside the 14-day cooldown (ADR-0003).</summary>
    public const string Image = "adobe/s3mock:5.2.3@sha256:ab01a6946750f451ca215a47e91030695b260e4003b8a5a6201d25029b8fca92";

    public const string Bucket = "tenantflow-test-files";

    /// <summary>S3Mock accepts any region; the adapter's settings still need one.</summary>
    public const string Region = "us-east-1";

    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);

    private string? _container;

    /// <summary>A client of the running S3Mock, standing in for the task role's client in deployed environments.</summary>
    public IAmazonS3 Client { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var cancel = TestContext.Current.CancellationToken;
        var run = await DockerCli.RunAsync(["run", "--detach", "--rm", "--publish", "127.0.0.1::9090", Image], cancel);
        Assert.True(run.ExitCode == 0, $"S3Mock did not start (exit {run.ExitCode}); is the Docker engine running? {run.Error}");
        _container = run.Output.Trim();

        var port = await DockerCli.RunAsync(["port", _container, "9090/tcp"], cancel);
        Assert.True(port.ExitCode == 0, $"S3Mock's port was not published (exit {port.ExitCode}): {port.Error}");
        var address = port.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];

        Client = new AmazonS3Client(
            new BasicAWSCredentials("placeholder", "placeholder"),
            new AmazonS3Config { ServiceURL = $"http://{address}", ForcePathStyle = true, AuthenticationRegion = Region });
        await CreateBucketWhenReadyAsync(cancel);
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        if (_container is not null)
        {
            await DockerCli.RunAsync(["rm", "--force", _container], CancellationToken.None);
        }
    }

    // The container answers a few seconds after it starts; the bucket is created on the first request that gets through.
    private async Task CreateBucketWhenReadyAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + StartTimeout;
        while (true)
        {
            try
            {
                await Client.PutBucketAsync(new PutBucketRequest { BucketName = Bucket }, cancellationToken);
                return;
            }
            catch (Exception exception) when ((exception is AmazonServiceException or HttpRequestException or IOException) && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            }
        }
    }
}
