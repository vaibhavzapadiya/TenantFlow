using System.Text.RegularExpressions;

namespace TenantFlow.Architecture.Tests;

/// <summary>
/// The deployed environment's alarms (deploy/terraform/environment/monitoring.tf) name the Worker's consumer queues as
/// text, so a consumer added or renamed in code would leave its dead-letter queue unwatched. Checked on every build.
/// </summary>
public sealed partial class DeployedAlarmTests
{
    private static readonly string Root = FindRoot();

    [Fact]
    public void Every_consumer_queue_has_a_dead_letter_alarm()
    {
        var monitoring = File.ReadAllText(Path.Combine(Root, "deploy", "terraform", "environment", "monitoring.tf"));
        var watched = AlarmQueues().Match(monitoring).Groups["queues"].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static queue => queue.Trim('"'))
            .Order(StringComparer.Ordinal);
        var consumers = Directory.GetFiles(Path.Combine(Root, "src", "Modules"), "*.cs", SearchOption.AllDirectories)
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(static path => ConsumerConstant().Matches(File.ReadAllText(path)).Select(static match => match.Groups["queue"].Value))
            .Order(StringComparer.Ordinal);

        Assert.Equal(consumers, watched);
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

    [GeneratedRegex(@"consumer_queues = \[(?<queues>[^\]]*)\]")]
    private static partial Regex AlarmQueues();

    [GeneratedRegex(@"public const string Consumer = ""(?<queue>[^""]+)"";")]
    private static partial Regex ConsumerConstant();
}
