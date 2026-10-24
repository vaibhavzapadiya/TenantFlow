using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Xml.Linq;

namespace TenantFlow.Architecture.Tests;

/// <summary>
/// SPEC-0015 S1: the repository is on the .NET 10 LTS toolchain ADR-0003 pins, and nothing is left behind from the
/// interim .NET 9 decision (ADR-0005). Checked on every build, so an SDK pin or a package family cannot drift back.
/// </summary>
public sealed class ToolchainTests
{
    private static readonly string Root = FindRoot();

    /// <summary>Families versioned against the runtime: none of them may stay on the 9 line (criterion 7).</summary>
    private static readonly string[] RuntimeAlignedPrefixes =
    [
        "Microsoft.AspNetCore.", "Microsoft.Extensions.", "Microsoft.EntityFrameworkCore.", "Npgsql",
    ];

    [Fact]
    public void The_sdk_is_pinned_to_a_patch_of_the_ten_zero_four_hundred_band()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "global.json")));
        var sdk = document.RootElement.GetProperty("sdk");

        Assert.Matches(@"^10\.0\.4\d\d$", sdk.GetProperty("version").GetString());
        // latestPatch takes a newer patch inside the band and refuses anything older, so an out-of-date workstation
        // fails to resolve an SDK instead of quietly building on it (criterion 2).
        Assert.Equal("latestPatch", sdk.GetProperty("rollForward").GetString());
        Assert.False(sdk.GetProperty("allowPrerelease").GetBoolean());
    }

    [Fact]
    public void Every_project_targets_net_ten_with_its_matching_analysis_level()
    {
        var properties = XDocument.Load(Path.Combine(Root, "Directory.Build.props")).Descendants().ToList();

        Assert.Equal("net10.0", properties.Single(static e => e.Name.LocalName == "TargetFramework").Value);
        Assert.Equal("10.0-recommended", properties.Single(static e => e.Name.LocalName == "AnalysisLevel").Value);
    }

    [Fact]
    public void The_tests_themselves_run_on_the_net_ten_runtime()
    {
        var framework = typeof(ToolchainTests).Assembly.GetCustomAttribute<TargetFrameworkAttribute>();

        Assert.Equal(".NETCoreApp,Version=v10.0", framework?.FrameworkName);
        Assert.Equal(10, Environment.Version.Major);
    }

    [Fact]
    public void No_runtime_aligned_package_is_left_on_the_nine_line()
    {
        var packages = XDocument.Load(Path.Combine(Root, "Directory.Packages.props"))
            .Descendants()
            .Where(static e => e.Name.LocalName == "PackageVersion")
            .Select(static e => (Id: e.Attribute("Include")!.Value, Version: e.Attribute("Version")!.Value))
            .Where(static p => RuntimeAlignedPrefixes.Any(prefix => p.Id.StartsWith(prefix, StringComparison.Ordinal)))
            .ToList();

        Assert.NotEmpty(packages);
        Assert.All(packages, package => Assert.False(
            package.Version.StartsWith("9.", StringComparison.Ordinal),
            $"{package.Id} is still on the .NET 9 line ({package.Version})."));
    }

    [Fact]
    public void No_host_hand_declares_the_entry_point_that_net_ten_generates()
    {
        var programs = Directory
            .EnumerateFiles(Path.Combine(Root, "src", "Hosts"), "Program.cs", SearchOption.AllDirectories)
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(programs);
        // .NET 10 emits the public Program class for top-level statements; declaring it again is ASP0027.
        Assert.All(programs, path => Assert.DoesNotContain(
            "partial class Program", File.ReadAllText(path), StringComparison.Ordinal));
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
