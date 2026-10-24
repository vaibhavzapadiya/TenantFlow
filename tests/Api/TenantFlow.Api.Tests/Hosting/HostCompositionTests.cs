using System.Net;
using Microsoft.Extensions.DependencyInjection;

namespace TenantFlow.Api.Tests.Hosting;

/// <summary>
/// Guards the host's composition rules. These tests run the host in the Production
/// environment on purpose: Development already enables scope and build validation by
/// default, so only a Production run proves that Program.cs turns them on everywhere.
/// </summary>
public sealed class HostCompositionTests
{
    // Was a 404 check in M1. Since SPEC-0002 criterion 8 the fallback policy also covers unmatched
    // paths, so an anonymous caller cannot tell mapped from unmapped routes.
    [Fact]
    public async Task Host_starts_and_denies_unmapped_routes_to_anonymous_callers()
    {
        await using var factory = ApiFactory.CreateProduction();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            new Uri("/route-that-does-not-exist", UriKind.Relative),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Host_refuses_to_start_when_a_registration_cannot_be_constructed()
    {
        await using var factory = ApiFactory.CreateProduction(
            static services => services.AddSingleton<ServiceWithMissingDependency>());

        var exception = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(exception);
        Assert.Contains(nameof(IMissingDependency), exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scoped_services_cannot_be_resolved_from_the_root_provider()
    {
        await using var factory = ApiFactory.CreateProduction(
            static services => services.AddScoped<ScopedProbe>());
        using var client = factory.CreateClient();

        var exception = Record.Exception(() => factory.Services.GetRequiredService<ScopedProbe>());

        Assert.IsType<InvalidOperationException>(exception);
    }

    public interface IMissingDependency;

    public sealed class ServiceWithMissingDependency(IMissingDependency dependency)
    {
        public IMissingDependency Dependency { get; } = dependency;
    }

    public sealed class ScopedProbe;
}
