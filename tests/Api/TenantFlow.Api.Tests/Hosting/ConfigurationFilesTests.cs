using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace TenantFlow.Api.Tests.Hosting;

/// <summary>
/// What the committed settings files may contain (pre-S5 review 2.7; AGENTS.md rule 6, ADR-0008 as amended by
/// ADR-0037). The base settings hold no secret and no address that differs per environment: those come from
/// environment variables or a secret store. The Development settings hold only the local stack's "-local-only"
/// values and loopback addresses; anything else comes from user-secrets.
/// </summary>
public sealed partial class ConfigurationFilesTests
{
    [GeneratedRegex("password|secret|connectionstring|apikey|api_key|token|authority|pwd", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ForbiddenKeyNames();

    [Fact]
    public async Task Base_settings_hold_no_secret_and_no_address_that_differs_per_environment()
    {
        await using var factory = ApiFactory.CreateProduction();
        using var client = factory.CreateClient();

        var forbidden = ReadSettingsFiles(factory)
            .Where(static file => !file.Name.EndsWith(".Development.json", StringComparison.Ordinal))
            .SelectMany(static file => KeysIn(file.Json).Where(key => ForbiddenKeyNames().IsMatch(key)).Select(key => $"{file.Name}: {key}"));

        Assert.Empty(forbidden);
    }

    [Fact]
    public async Task Development_settings_hold_only_local_only_credentials_and_loopback_addresses()
    {
        // ADR-0037 (amending ADR-0008): Development settings carry the local stack's throwaway values, so the API starts
        // after "docker compose up" with no configuration step. A credential there must be a "-local-only" value and an
        // address a loopback one; anything real belongs in user-secrets.
        await using var factory = ApiFactory.CreateProduction();
        using var client = factory.CreateClient();

        var offending = ReadSettingsFiles(factory)
            .Where(static file => file.Name.EndsWith(".Development.json", StringComparison.Ordinal))
            .SelectMany(static file => ValuesIn(file.Json)
                .Where(static setting => ForbiddenKeyNames().IsMatch(setting.Key) && !IsLocalOnly(setting.Value))
                .Select(setting => $"{file.Name}: {setting.Key}"));

        Assert.Empty(offending);
    }

    private static bool IsLocalOnly(string value) =>
        value.Contains("-local-only", StringComparison.Ordinal)
        || (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsLoopback);

    // The string settings of a file as configuration keys ("Section:Key") with their values.
    private static IEnumerable<(string Key, string Value)> ValuesIn(JsonElement element, string prefix = "")
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            yield return (prefix, element.GetString() ?? string.Empty);
            yield break;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var property in element.EnumerateObject())
        {
            foreach (var value in ValuesIn(property.Value, prefix.Length == 0 ? property.Name : prefix + ":" + property.Name))
            {
                yield return value;
            }
        }
    }

    [Fact]
    public async Task Production_logging_does_not_record_sql_or_framework_chatter_at_information()
    {
        await using var factory = ApiFactory.CreateProduction();
        using var client = factory.CreateClient();

        var levels = ReadSettingsFiles(factory).Single(static file => file.Name == "appsettings.json").Json
            .GetProperty("Logging").GetProperty("LogLevel");

        // EF Core logs every executed command, SQL text included, at Information (blueprint section 13: logs
        // are not a secondary database).
        Assert.True(levels.TryGetProperty("Microsoft.EntityFrameworkCore", out var entityFramework), "no level is set for EF Core");
        Assert.Equal("Warning", entityFramework.GetString());
        Assert.True(levels.TryGetProperty("Microsoft.AspNetCore", out var aspNetCore), "no level is set for ASP.NET Core");
        Assert.Equal("Warning", aspNetCore.GetString());
    }

    // SPEC-0009 criterion 19: outside Development, EF Core logs nothing below Warning, so SQL text never reaches the logs.
    [Fact]
    public async Task Outside_development_entity_framework_core_logs_warnings_and_above_only()
    {
        await using var factory = ApiFactory.CreateProduction();

        var logger = factory.Services.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>().CreateLogger("Microsoft.EntityFrameworkCore.Database.Command");

        Assert.Equal((false, true), (logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Information), logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Warning)));
    }

    private static List<(string Name, JsonElement Json)> ReadSettingsFiles(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory)
    {
        var root = factory.Services.GetRequiredService<IWebHostEnvironment>().ContentRootPath;
        var files = Directory.GetFiles(root, "appsettings*.json");
        Assert.NotEmpty(files);
        return [.. files.Select(static path => (Path.GetFileName(path), JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone()))];
    }

    private static IEnumerable<string> KeysIn(JsonElement element, string prefix = "")
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var property in element.EnumerateObject())
        {
            var path = prefix.Length == 0 ? property.Name : prefix + ":" + property.Name;
            yield return path;
            foreach (var nested in KeysIn(property.Value, path))
            {
                yield return nested;
            }
        }
    }
}
