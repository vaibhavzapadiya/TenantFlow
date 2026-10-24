using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Npgsql;
using TenantFlow.BuildingBlocks.Persistence.Retention;
using TenantFlow.Tenants.Contracts;
using TenantFlow.Worker.Retention;

namespace TenantFlow.Worker.Tests;

/// <summary>
/// SPEC-0009 criterion 8: the Worker's retention run applies every rule every module declares. A module that adds a
/// <c>…Retention</c> class the task does not include fails here; the rules themselves are proven in <c>RetentionTests</c>.
/// </summary>
public sealed class RetentionTaskTests
{
    [Fact]
    public void The_retention_run_applies_every_rule_of_every_module_once()
    {
        var declared = typeof(RetentionTask).Assembly.GetReferencedAssemblies()
            .Where(static name => name.Name!.StartsWith("TenantFlow.", StringComparison.Ordinal) && name.Name.EndsWith(".Infrastructure", StringComparison.Ordinal))
            .Select(Assembly.Load)
            .SelectMany(static assembly => assembly.GetExportedTypes())
            .Where(static type => type.Name.EndsWith("Retention", StringComparison.Ordinal))
            .Select(static type => type.GetProperty("Rules", BindingFlags.Public | BindingFlags.Static)?.GetValue(null))
            .OfType<IReadOnlyList<RetentionRule>>()
            .SelectMany(static rules => rules)
            .Select(static rule => rule.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(declared);
        Assert.Equal(declared, RetentionTask.Rules.Select(static rule => rule.Name).Order(StringComparer.Ordinal));
        Assert.Equal(RetentionTask.Rules.Count, RetentionTask.Rules.Select(static rule => rule.Name).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task A_tenant_that_fails_is_logged_and_skipped_so_the_tenants_after_it_still_run()
    {
        // Nothing listens on port 1, so every tenant's rules fail to connect: the run must try them all and not throw.
        await using var unreachable = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Timeout=1;Database=none;Username=none");
        var logger = new FakeLogger();

        var (changed, failed) = await RetentionTask.RunAllAsync(
            new TwoTenants(), unreachable, DateTimeOffset.UnixEpoch, logger, TestContext.Current.CancellationToken);

        Assert.Equal(0, changed);
        Assert.Equal(2, failed);
        Assert.Equal(2, logger.Collector.GetSnapshot().Count(static r => r.Level == LogLevel.Error));
    }

    private sealed class TwoTenants : ITenantDirectory
    {
        private static readonly Guid[] Ids = [Guid.CreateVersion7(), Guid.CreateVersion7()];

        public Task<IReadOnlyDictionary<Guid, TenantAvailability>> GetAvailabilityAsync(IReadOnlyCollection<Guid> tenantIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Guid>> ListIdsAsync(Guid? after, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Guid>>(after is null ? Ids : []);
    }
}
