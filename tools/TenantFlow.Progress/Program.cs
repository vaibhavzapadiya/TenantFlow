using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TenantFlow.Progress;

/// <summary>
/// dotnet run --project tools/TenantFlow.Progress [-- verify] [open]: builds artifacts/progress/dashboard.html
/// (git-ignored) from docs/progress/progress.json and live evidence of this checkout. "verify" first runs the gate on the
/// current commit and records it in artifacts/progress/quality.json; "open" opens the dashboard. Exit code 1 when
/// progress.json is invalid or a threat-model action is incomplete.
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };
        var token = cancellation.Token;
        var options = args.Select(static arg => arg.TrimStart('-').ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);

        var root = FindRoot();
        var outDir = Path.Combine(root, "artifacts", "progress");
        var qualityFile = Path.Combine(outDir, "quality.json");
        var outFile = Path.Combine(outDir, "dashboard.html");
        Directory.CreateDirectory(outDir);

        var rawData = await File.ReadAllTextAsync(Path.Combine(root, "docs", "progress", "progress.json"), token);
        JsonNode data;
        try
        {
            data = JsonNode.Parse(rawData) ?? throw new JsonException("The document is empty.");
        }
        catch (JsonException exception)
        {
            await Console.Error.WriteLineAsync($"docs/progress/progress.json is not valid JSON: {exception.Message}");
            return 1;
        }

        if (options.Contains("verify"))
        {
            var run = await QualityRun.RunAsync(root, outDir, token);
            await File.WriteAllTextAsync(qualityFile, run.ToJsonString(Indented), new UTF8Encoding(false), token);
        }

        WriteStep("Collecting repository evidence");
        var threatActions = Evidence.ThreatActions(root, data);
        var evidence = new JsonObject
        {
            ["generatedAt"] = Now(),
            ["git"] = await Evidence.GitAsync(root, token),
            ["registers"] = Evidence.Registers(root),
            ["testInventory"] = Evidence.TestInventory(root),
            ["commitsExist"] = await Evidence.CommitsExistAsync(root, data, token),
            ["threatActions"] = threatActions,
            ["quality"] = File.Exists(qualityFile) ? JsonNode.Parse(await File.ReadAllTextAsync(qualityFile, token)) : null,
        };

        var threatErrors = threatActions["errors"]!.AsArray().Select(static error => error!.GetValue<string>()).ToList();
        foreach (var error in threatErrors)
        {
            await Console.Error.WriteLineAsync($"Threat-model action: {error}");
        }

        // Both documents are embedded as JSON; "</" is escaped so the data can never close the script element.
        var template = await File.ReadAllTextAsync(Path.Combine(root, "tools", "TenantFlow.Progress", "template.html"), token);
        var html = template
            .Replace("/*PROGRESS_DATA*/", rawData.Replace("</", "<\\/", StringComparison.Ordinal), StringComparison.Ordinal)
            .Replace("/*EVIDENCE_DATA*/", evidence.ToJsonString(Indented).Replace("</", "<\\/", StringComparison.Ordinal), StringComparison.Ordinal);
        await File.WriteAllTextAsync(outFile, html, new UTF8Encoding(false), token);
        WriteStep("Dashboard written to artifacts/progress/dashboard.html");

        if (options.Contains("open"))
        {
            // The default browser on Windows, macOS and Linux (xdg-open).
            using var browser = Process.Start(new ProcessStartInfo(outFile) { UseShellExecute = true });
        }

        if (threatErrors.Count > 0)
        {
            await Console.Error.WriteLineAsync($"Threat-model action check failed: {threatErrors.Count} error(s).");
            return 1;
        }

        WriteStep(string.Create(CultureInfo.InvariantCulture, $"Threat-model actions: {threatActions["total"]} in {threatActions["models"]} models ({threatActions["done"]} done, {threatActions["planned"]} planned, {threatActions["accepted"]} accepted)"));
        return 0;
    }

    public static string Now() => DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    public static void WriteStep(string message) => Console.WriteLine($"==> {message}");

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TenantFlow.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Run this from inside the TenantFlow repository.");
    }
}
