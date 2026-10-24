using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;

[assembly: AssemblyFixture(typeof(TenantFlow.Drills.Tests.DrillEnvironment))]

namespace TenantFlow.Drills.Tests;

/// <summary>
/// SPEC-0010 criterion 14 (decision 8): one test per fault. Each checks that its alert is not already active, injects the
/// fault, waits for the alert, writes its runbook, undoes the fault and, where the fault can be undone, waits for the
/// alert to resolve. Run one at a time, with the observability profile and both hosts running, and record the times in
/// docs/operations/drills.
/// </summary>
public sealed class FaultDrills(DrillEnvironment stack)
{
    private static readonly TimeSpan Recovery = TimeSpan.FromMinutes(10);

    [Fact]
    public Task Database_down_fires_DatabaseDown() => DrillAsync("DatabaseDown", TimeSpan.FromMinutes(4),
        inject: token => DrillEnvironment.ComposeAsync("stop", "postgres", token),
        undo: token => DrillEnvironment.ComposeAsync("start", "postgres", token));

    [Fact]
    public Task Broker_down_fires_BrokerDown() => DrillAsync("BrokerDown", TimeSpan.FromMinutes(4),
        inject: token => DrillEnvironment.ComposeAsync("stop", "rabbitmq", token),
        undo: token => DrillEnvironment.ComposeAsync("start", "rabbitmq", token));

    // Three payments the simulator never settles (sim_timeout_unknown): reconciliation checks them and they stay open.
    // Nothing to undo: the alert resolves when its window passes (see the runbook for the clean-up).
    [Fact]
    public Task Provider_unknown_outcomes_fire_PaymentsStuck() => DrillAsync("PaymentsStuck", TimeSpan.FromMinutes(15),
        inject: async token =>
        {
            var (tenantId, productId, user) = await NewDrillTenantAsync(token);
            for (var i = 0; i < 3; i++)
            {
                var order = await stack.ApiAsync(HttpMethod.Post, $"/v1/tenants/{tenantId}/orders", user, new { lines = new[] { new { productId, quantity = 1 } } }, token);
                var orderId = order?["id"]?.GetValue<string>();
                await stack.ApiAsync(HttpMethod.Post, $"/v1/tenants/{tenantId}/orders/{orderId}/submit", user, null, token);
                await stack.ApiAsync(HttpMethod.Post, $"/v1/tenants/{tenantId}/orders/{orderId}/payments", user, new { paymentMethod = "sim_timeout_unknown" }, token, idempotencyKey: Guid.NewGuid().ToString());
            }

            DrillEnvironment.Note($"three payments with an unknown outcome in tenant {tenantId}");
        },
        undo: null);

    // The Worker process is stopped, then started again from the build output once the alert fires.
    [Fact]
    public Task Worker_down_fires_WorkerTelemetryMissing() => DrillAsync("WorkerTelemetryMissing", TimeSpan.FromMinutes(10),
        inject: _ =>
        {
            var workers = Process.GetProcessesByName("TenantFlow.Worker");
            Assert.True(workers.Length > 0, "No TenantFlow.Worker process was found to stop.");
            foreach (var worker in workers)
            {
                using (worker)
                {
                    worker.Kill(entireProcessTree: true);
                }
            }

            return Task.CompletedTask;
        },
        undo: _ =>
        {
            using var restarted = Process.Start(new ProcessStartInfo("dotnet", ["run", "--project", "src/Hosts/TenantFlow.Worker", "--no-build"]) { WorkingDirectory = DockerCli.RepositoryRoot, UseShellExecute = false })
                ?? throw new InvalidOperationException("The Worker did not start again; start it with: dotnet run --project src/Hosts/TenantFlow.Worker");
            DrillEnvironment.Note($"Worker started again (process {restarted.Id}); stop it when the drill is recorded.");
            return Task.CompletedTask;
        });

    // A message without a valid envelope: the consumer dead-letters it at once (ADR-0021), never retries it.
    [Fact]
    public Task Poison_message_fires_DeadLettersGrowing() => DrillAsync("DeadLettersGrowing", TimeSpan.FromMinutes(4),
        inject: token => stack.PublishAsync("notifications.order-events", "drill-poison", "drill.poison", "not an envelope", token),
        undo: null);

    // A member of one tenant naming tenants it does not belong to: refused with not_a_member, counted and alerted.
    [Fact]
    public Task Cross_tenant_probing_fires_CrossTenantProbing() => DrillAsync("CrossTenantProbing", TimeSpan.FromMinutes(8),
        inject: async token =>
        {
            var user = await stack.TokenAsync("tenantflow", "tenantflow-dev-cli", "dev-user-a", token);
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(6);
            while (DateTimeOffset.UtcNow < deadline && await stack.ActiveAlertAsync("CrossTenantProbing", token) is null)
            {
                for (var i = 0; i < 10; i++)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"{DrillEnvironment.Api}/v1/tenants/{Guid.NewGuid()}/products"));
                    request.Headers.Authorization = user;
                    using var response = await stack.Http.SendAsync(request, token);
                }

                await Task.Delay(TimeSpan.FromSeconds(20), token);
            }
        },
        undo: null);

    private async Task DrillAsync(string alertName, TimeSpan within, Func<CancellationToken, Task> inject, Func<CancellationToken, Task>? undo)
    {
        var token = TestContext.Current.CancellationToken;
        Assert.True(await stack.ActiveAlertAsync(alertName, token) is null, $"{alertName} is already active; let it resolve before the drill.");

        DrillEnvironment.Note($"expecting {alertName} within {within.TotalMinutes.ToString(CultureInfo.InvariantCulture)} minutes");
        var start = DateTimeOffset.UtcNow;
        DrillEnvironment.Log("fault injected", start);
        System.Text.Json.Nodes.JsonNode? alert;
        try
        {
            await inject(token);
            alert = await stack.WaitForAlertAsync(alertName, within, resolved: false, token);
            if (alert is not null)
            {
                DrillEnvironment.Log($"{alertName} active", start);
                DrillEnvironment.Note($"runbook: {alert["annotations"]?["runbook"]}");
                DrillEnvironment.Note($"summary: {alert["annotations"]?["summary"]}");
            }
        }
        finally
        {
            // A fault that stops a shared dependency is always undone, even when the drill fails.
            if (undo is not null)
            {
                await undo(token);
            }
        }

        if (undo is not null)
        {
            var undone = DateTimeOffset.UtcNow;
            var resolved = await stack.WaitForAlertAsync(alertName, Recovery, resolved: true, token);
            DrillEnvironment.Log(resolved is null ? $"{alertName} still active after 10 min" : $"{alertName} resolved", undone);
        }
        else
        {
            DrillEnvironment.Note("Nothing to undo: the alert resolves when its window passes (see the runbook for the clean-up).");
        }

        Assert.True(alert is not null, $"{alertName} did not fire within {within.TotalMinutes.ToString(CultureInfo.InvariantCulture)} minutes.");
    }

    // A tenant with dev-user-a as its administrator and one product in stock, created through the API as an operator would.
    private async Task<(string TenantId, string ProductId, AuthenticationHeaderValue User)> NewDrillTenantAsync(CancellationToken token)
    {
        var operatorToken = await stack.TokenAsync("tenantflow-platform", "tenantflow-platform-dev-cli", "dev-operator", token);
        var user = await stack.TokenAsync("tenantflow", "tenantflow-dev-cli", "dev-user-a", token);
        var tenant = await stack.ApiAsync(HttpMethod.Post, "/v1/platform/tenants", operatorToken, new { name = $"Drill {DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}" }, token);
        var tenantId = tenant?["id"]?.GetValue<string>() ?? throw new InvalidOperationException("The API returned no tenant id.");
        var subject = (await stack.ApiAsync(HttpMethod.Get, "/v1/me", user, null, token))?["subject"]?.GetValue<string>();
        await stack.ApiAsync(HttpMethod.Post, $"/v1/platform/tenants/{tenantId}/admins", operatorToken, new { subject }, token);
        var product = await stack.ApiAsync(HttpMethod.Post, $"/v1/tenants/{tenantId}/products", user, new { sku = $"DRILL-{Guid.NewGuid().ToString("N")[..8]}", name = "Drill product", price = new { amount = 5, currency = "EUR" } }, token);
        var productId = product?["id"]?.GetValue<string>() ?? throw new InvalidOperationException("The API returned no product id.");
        await stack.ApiAsync(HttpMethod.Post, $"/v1/tenants/{tenantId}/products/{productId}/stock/adjustments", user, new { delta = 100, reason = "Drill stock" }, token);
        return (tenantId, productId, user);
    }
}
