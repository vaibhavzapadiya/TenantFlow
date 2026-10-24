using System.Text.Json;
using System.Text.RegularExpressions;

namespace TenantFlow.Architecture.Tests;

/// <summary>
/// SPEC-0010: the observability files in the repository, checked on every build. The running stack is read back by
/// the smoke tests (<c>tests/Smoke</c>); these tests keep the committed configuration from drifting first.
/// </summary>
public sealed class ObservabilityConfigTests
{
    private static readonly string Root = FindRoot();
    private static readonly string Config = Path.Combine(Root, "deploy", "compose", "config");

    // Rules and dashboards are kept apart from the local stack's runtime configuration, for reuse by a deployed backend.
    private static readonly string Observability = Path.Combine(Root, "deploy", "observability");
    private static readonly string[] DataSources = ["prometheus", "loki"];

    // Alert annotations may template only these closed label values.
    private static readonly HashSet<string> AllowedTemplates = new(StringComparer.Ordinal)
    {
        "{{ $labels.service_name }}", "{{ $labels.module }}", "{{ $labels.consumer }}", "{{ $labels.job }}", "{{ $labels.kind }}",
    };

    [Fact]
    public void Loki_tempo_and_prometheus_are_configured_to_keep_7_days()
    {
        // Criterion 8 (ADR-0025, DP-A12).
        var compose = File.ReadAllText(Path.Combine(Root, "deploy", "compose", "compose.yaml"));
        var loki = File.ReadAllText(Path.Combine(Config, "loki.yaml"));
        var tempo = File.ReadAllLines(Path.Combine(Config, "tempo.yaml"));

        Assert.Contains("- --storage.tsdb.retention.time=7d", compose, StringComparison.Ordinal);
        Assert.Contains("- ./config/loki.yaml:/etc/loki/loki.yaml:ro", compose, StringComparison.Ordinal);
        Assert.Contains("retention_enabled: true", loki, StringComparison.Ordinal);
        Assert.Contains("retention_period: 168h", loki, StringComparison.Ordinal);
        Assert.Equal(2, tempo.Count(static line => line.Trim() == "block_retention: 168h"));
    }

    [Theory]
    // Criterion 1: rate, 5xx and 4xx apart, p50/p95/p99 per route and method, CPU, GC, thread pool and Npgsql pool per host.
    [InlineData("tenantflow-golden-signals",
        "rate(http_server_request_duration_seconds_count{service_name=\"tenantflow-api\"}",
        "http_response_status_code=~\"5..\"", "http_response_status_code=~\"4..\"",
        "histogram_quantile(0.5,", "histogram_quantile(0.95,", "histogram_quantile(0.99,", "by (le, http_route, http_request_method)",
        "dotnet_process_cpu_time_seconds_total", "dotnet_gc_pause_time_seconds_total", "dotnet_thread_pool_queue_length_total",
        "db_client_connections_usage")]
    // Criterion 3: queue depth, dead letters, consumed by outcome, outbox age per module, job runs, duration and lateness,
    // dead timers.
    [InlineData("tenantflow-worker",
        "rabbitmq_detailed_queue_messages{queue!~", "rabbitmq_detailed_queue_messages{queue=~\".*\\\\.dlq\"}",
        "sum by (consumer, outcome) (rate(tenantflow_messaging_consumed_total", "max by (module) (tenantflow_outbox_oldest_pending_age_seconds)",
        "sum by (job, outcome) (increase(tenantflow_scheduling_runs_total", "tenantflow_scheduling_duration_seconds_bucket",
        "tenantflow_scheduling_lateness_seconds_bucket", "tenantflow_messaging_dead_timers")]
    // Criterion 4: payments by outcome, unknown and unresolved, oldest unresolved, provider latency, refunds, reversals,
    // reservation conflicts, import/export age, duration and failed rows.
    [InlineData("tenantflow-business",
        "sum by (outcome) (increase(tenantflow_payments_attempts_total", "tenantflow_payments_unresolved",
        "tenantflow_payments_oldest_unresolved_age_seconds", "tenantflow_payments_provider_duration_seconds_bucket",
        "tenantflow_refunds_completed_total{outcome!=\"reversed\"}", "tenantflow_refunds_completed_total{outcome=\"reversed\"}",
        "sum by (reason) (increase(tenantflow_orders_reservation_conflicts_total", "tenantflow_imports_exports_oldest_due_age_seconds",
        "tenantflow_imports_exports_job_duration_seconds_bucket", "tenantflow_imports_exports_failed_rows_total")]
    public void Each_dashboard_is_provisioned_from_the_repository_with_the_queries_its_criterion_needs(string uid, params string[] queries)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Observability, "grafana", "dashboards", uid + ".json")));
        var dashboard = document.RootElement;
        var targets = dashboard.GetProperty("panels").EnumerateArray()
            .Where(static panel => panel.TryGetProperty("targets", out _))
            .SelectMany(static panel => panel.GetProperty("targets").EnumerateArray().Select(target => (Panel: panel, Target: target)))
            .ToList();
        var expressions = targets.Select(static t => t.Target.GetProperty("expr").GetString()!).ToList();

        Assert.Equal(uid, dashboard.GetProperty("uid").GetString());
        Assert.False(dashboard.GetProperty("editable").GetBoolean());
        Assert.All(queries, query => Assert.Contains(expressions, expression => expression.Contains(query, StringComparison.Ordinal)));
        // Only the provisioned data sources, and never a tenant, subject or id in a query (blueprint section 16).
        Assert.All(targets, static t => Assert.Contains(t.Target.GetProperty("datasource").GetProperty("uid").GetString(), DataSources));
        Assert.All(expressions, static expression => Assert.DoesNotMatch(@"(?i)\btenant(_id)?\b|\bsubject\b|\w_id\b", expression));
    }

    [Fact]
    public void Grafana_loads_the_dashboards_folder_read_only()
    {
        var compose = File.ReadAllText(Path.Combine(Root, "deploy", "compose", "compose.yaml"));
        var provider = File.ReadAllText(Path.Combine(Config, "grafana", "provisioning", "dashboards", "dashboards.yaml"));

        Assert.Contains("- ../observability/grafana/dashboards:/etc/grafana/dashboards/tenantflow:ro", compose, StringComparison.Ordinal);
        Assert.Contains("allowUiUpdates: false", provider, StringComparison.Ordinal);
        Assert.Contains("path: /etc/grafana/dashboards/tenantflow", provider, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_slo_document_has_the_blueprint_fields_and_a_recording_rule_for_each_window()
    {
        // Criterion 9 (ADR-0030): blueprint section 16 fields, a provisional target, and a sync or async kind.
        string[] headings =
        [
            "## User-visible event and window", "## Success and latency definition", "## Eligible traffic and exclusions",
            "## Target and error budget", "## Alert policy", "## Dependency and capacity assumptions",
        ];
        var rules = File.ReadAllText(Path.Combine(Observability, "prometheus", "rules", "slo.rules.yml"));
        var documents = Directory.GetFiles(Path.Combine(Root, "docs", "operations", "slos"), "*.md")
            .Where(static path => Path.GetFileName(path) != "README.md")
            .ToList();

        Assert.Equal(["catalog-read", "event-delivery", "export-completion", "order-submission", "payment-resolution"],
            documents.Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal));
        Assert.All(documents, path =>
        {
            var text = File.ReadAllText(path);
            var slo = Path.GetFileNameWithoutExtension(path).Replace('-', '_');
            Assert.All(headings, heading => Assert.Contains(heading + "\n", text.ReplaceLineEndings("\n"), StringComparison.Ordinal));
            Assert.Contains("**provisional**", text, StringComparison.Ordinal);
            Assert.Matches(@"- Kind: (Synchronous|Asynchronous) \(", text);
            Assert.Contains($"slo: {slo}", rules, StringComparison.Ordinal);
        });
        foreach (var window in new[] { "5m", "30m", "1h", "6h" })
        {
            Assert.Equal(documents.Count, rules.Split($"record: tenantflow:slo_error_ratio:rate{window}\n").Length - 1);
        }
    }

    [Fact]
    public void Every_alert_has_a_severity_a_category_and_a_runbook_and_quotes_no_value()
    {
        // ADR-0029 and the SPEC-0010 contract: severity page or ticket, a cause/symptom/event category for inhibition, a
        // runbook path, and annotations that template only closed label values, never $value or a request's data.
        var rules = File.ReadAllText(Path.Combine(Observability, "prometheus", "rules", "alerts.rules.yml")).ReplaceLineEndings("\n");
        var alerts = rules.Split("\n      - alert: ").Skip(1).ToList();

        Assert.True(alerts.Count >= 20, $"Only {alerts.Count} alerts were found.");
        Assert.All(alerts, alert =>
        {
            var name = alert[..alert.IndexOf('\n', StringComparison.Ordinal)];
            Assert.Matches(@"\n {10}severity: (page|ticket)\n", alert);
            Assert.Matches(@"\n {10}category: (cause|symptom|event)\n", alert);
            Assert.Matches(@"\n {10}runbook: docs/operations/runbooks/[a-z-]+\.md(\n|$)", alert);
            Assert.DoesNotContain("$value", alert, StringComparison.Ordinal);
            foreach (Match template in Regex.Matches(alert, @"\{\{[^}]*\}\}"))
            {
                Assert.True(AllowedTemplates.Contains(template.Value), $"{name} templates {template.Value}");
            }
        });
    }

    // ADR-0029: every rule file has promtool tests beside it, which the Compose "rule-tests" service runs.
    [Fact]
    public void Every_rule_file_has_a_promtool_test_file()
    {
        var directory = Path.Combine(Observability, "prometheus", "rules");
        var ruleFiles = Directory.GetFiles(directory, "*.rules.yml");

        Assert.NotEmpty(ruleFiles);
        Assert.All(ruleFiles, file => Assert.True(
            File.Exists(Path.ChangeExtension(file, ".test.yml")),
            $"{Path.GetFileName(file)} has no {Path.GetFileNameWithoutExtension(file)}.test.yml"));
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
