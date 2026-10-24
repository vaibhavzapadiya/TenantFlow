using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TenantFlow.Audit.Contracts;
using TenantFlow.BuildingBlocks.Abstractions.Identity;
using TenantFlow.IdentityAccess.Application.Memberships.Commands;
using TenantFlow.IdentityAccess.Application.Memberships.Dtos;
using TenantFlow.IdentityAccess.Application.Memberships.Services;
using TenantFlow.IdentityAccess.Application.TenantAccess;
using TenantFlow.IdentityAccess.Infrastructure;
using TenantFlow.Tenants.Application.Lifecycle.Commands;
using TenantFlow.Tenants.Application.Lifecycle.Services;
using TenantFlow.Tenants.Infrastructure;

namespace TenantFlow.Database.Tests.Support;

/// <summary>
/// The IdentityAccess and Tenants modules wired the way the API wires them, against one test database, with a
/// controllable clock. Every call runs in its own scope, like a request.
/// </summary>
internal sealed class TenantHarness : IAsyncDisposable
{
    public const string Issuer = "https://idp.test/realms/tenantflow";
    public const string OtherIssuer = "https://other-idp.test/realms/tenantflow";

    public static readonly ActorIdentity Operator = new("https://idp.test/realms/tenantflow-platform", "operator-1");

    /// <summary>The operator's reference in the central audit log: a fixed synthetic value, not a hash of a real key.</summary>
    public static readonly AuditActor OperatorAudit = AuditActor.PlatformAdmin(new string('0', AuditActor.OperatorReferenceLength));

    private readonly ServiceProvider _provider;

    private TenantHarness(ServiceProvider provider, FakeTimeProvider clock)
    {
        _provider = provider;
        Clock = clock;
    }

    public FakeTimeProvider Clock { get; }

    public static TenantHarness Create(TestDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 19, 9, 0, 0, TimeSpan.Zero));
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<TimeProvider>(clock);
        services.AddTenantsModule(database.AppConnectionString);
        services.AddIdentityAccessModule(database.AppConnectionString);
        return new TenantHarness(services.BuildServiceProvider(validateScopes: true), clock);
    }

    public async Task<T> RunAsync<T>(Func<MembershipService, Task<T>> action)
    {
        await using var scope = _provider.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<MembershipService>());
    }

    /// <summary>
    /// Creates an active tenant. Tenant names are unique (SPEC-0002 criterion 26) and tests share a database, so the
    /// name given is a prefix: a unique suffix is added.
    /// </summary>
    public async Task<Guid> CreateTenantAsync(string name = "Acme")
    {
        name = $"{name} {Guid.NewGuid():N}";
        await using var scope = _provider.CreateAsyncScope();
        var tenant = await scope.ServiceProvider.GetRequiredService<TenantLifecycleService>()
            .CreateAsync(new CreateTenantCommand(name), Operator, OperatorAudit, TestContext.Current.CancellationToken);
        return tenant.Id;
    }

    public async Task SuspendTenantAsync(Guid tenantId)
    {
        await using var scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TenantLifecycleService>()
            .SuspendAsync(tenantId, Operator, OperatorAudit, TestContext.Current.CancellationToken);
    }

    public Task<MembershipDetails> AddAsync(Guid tenantId, string subject, string role, string issuer = Issuer, ActorIdentity? actor = null) =>
        RunAsync(service => service.AddAsync(
            new AddMemberCommand(tenantId, issuer, subject, role), actor ?? Operator, OperatorAudit, TestContext.Current.CancellationToken));

    public Task<MembershipDetails> ChangeRoleAsync(Guid tenantId, Guid membershipId, string role, ActorIdentity? actor = null) =>
        RunAsync(service => service.ChangeRoleAsync(
            new ChangeRoleCommand(tenantId, membershipId, role), actor ?? Operator, OperatorAudit, TestContext.Current.CancellationToken));

    public Task RemoveAsync(Guid tenantId, Guid membershipId, ActorIdentity? actor = null) =>
        RunAsync(async service =>
        {
            await service.RemoveAsync(tenantId, membershipId, actor ?? Operator, OperatorAudit, TestContext.Current.CancellationToken);
            return 0;
        });

    public Task<IReadOnlyList<MembershipSummary>> ListAsync(string issuer, string subject) =>
        RunAsync(service => service.ListForIdentityAsync(new ActorIdentity(issuer, subject), TestContext.Current.CancellationToken));

    /// <summary>Resolves the caller's membership in a tenant in a fresh scope (like one request) and returns the role found.</summary>
    public async Task<string> ResolveRoleAsync(Guid tenantId, ActorIdentity caller, string permission)
    {
        await using var scope = _provider.CreateAsyncScope();
        var resolver = scope.ServiceProvider.GetRequiredService<TenantAccessResolver>();
        await resolver.ResolveAsync(tenantId, caller, permission, TestContext.Current.CancellationToken);
        return resolver.Role;
    }

    public ValueTask DisposeAsync() => _provider.DisposeAsync();
}
