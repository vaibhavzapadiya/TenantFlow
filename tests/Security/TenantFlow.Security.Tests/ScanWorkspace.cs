namespace TenantFlow.Security.Tests;

/// <summary>
/// The scanners and the folder they read (ADR-0028 decision 2): images pinned by digest, and a temporary folder that
/// holds copies of what is scanned, so a scanner never sees an ignored file such as a local <c>.env</c>.
/// </summary>
public sealed class ScanWorkspace : IDisposable
{
    // Pinned by digest. Dependabot proposes upgrades; the tag is kept for readers (SecurityScanTests checks the pins).
    public const string GitleaksImage = "zricethezav/gitleaks:v8.30.1@sha256:c00b6bd0aeb3071cbcb79009cb16a60dd9e0a7c60e2be9ab65d25e6bc8abbb7f";
    public const string TrivyImage = "aquasec/trivy:0.75.0@sha256:af6acf9a6b85dfe389a1941505c0ce9efef52a4719635e1a962f022a3d855daa";

    // ghcr.io, not Trivy's default mirror.gcr.io, which some networks refuse.
    public static readonly string[] TrivyRepositories = ["--db-repository", "ghcr.io/aquasecurity/trivy-db:2", "--java-db-repository", "ghcr.io/aquasecurity/trivy-java-db:1"];

    // The Docker host's CA bundle, when it has the usual Debian/Ubuntu one (WSL Ubuntu, GitHub's Ubuntu runners), so a
    // TLS-inspecting proxy the host trusts works, and nothing else is trusted.
    private const string CaBundle = "/etc/ssl/certs/ca-certificates.crt";

    public string Folder { get; } = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "tenantflow-scan-" + Guid.NewGuid().ToString("N"))).FullName;

    /// <summary>docker run arguments that mount the folder at /scan (read-only unless writable), the Trivy cache and the CA bundle.</summary>
    public async Task<List<string>> MountsAsync(bool writable, CancellationToken cancellationToken)
    {
        var mounts = new List<string> { "-v", $"{await DockerCli.EnginePathAsync(Folder, cancellationToken)}:/scan{(writable ? string.Empty : ":ro")}", "-v", "tenantflow-trivy-cache:/root/.cache" };
        if (await DockerCli.EngineFileExistsAsync(CaBundle, cancellationToken))
        {
            mounts.AddRange(["-v", $"{CaBundle}:{CaBundle}:ro"]);
        }

        return mounts;
    }

    /// <summary>Copies files into the folder, keeping their paths relative to the repository root.</summary>
    public void Copy(IEnumerable<string> relativePaths, string into)
    {
        ArgumentNullException.ThrowIfNull(relativePaths);
        foreach (var relative in relativePaths)
        {
            var source = Path.Combine(DockerCli.RepositoryRoot, relative);
            if (!File.Exists(source))
            {
                continue;
            }

            var target = Path.Combine(Folder, into, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
        }
    }

    public void Dispose()
    {
        try
        {
            // Git writes its pack files read-only, which Windows refuses to delete.
            foreach (var file in Directory.EnumerateFiles(Folder, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Folder, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A scanner may still hold a file; the folder is in the temporary directory.
        }
    }
}
