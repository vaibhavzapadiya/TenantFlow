using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TenantFlow.Api.Endpoints.V1.TenantAccess;
using TenantFlow.Api.Extensions;
using TenantFlow.IdentityAccess.Contracts;

namespace TenantFlow.Api.Tests.Routing;

/// <summary>
/// SPEC-0002 criterion 22: the route table is an exact list. Every endpoint is either a health probe (anonymous), a
/// <c>/me</c> route, a platform route, or a tenant route, and each kind carries the policy that makes it so; a tenant
/// route also names the permission it needs. A new endpoint therefore fails this test until it is added here, in the
/// same change as its own tests, so no route can reach production without a reviewed policy.
/// </summary>
public sealed class RouteTableTests
{
    private const string Tenant = AuthenticationExtensions.TenantAuthenticatedUserPolicy;
    private const string Platform = AuthenticationExtensions.PlatformAdminPolicy;
    private const string Support = AuthenticationExtensions.SupportOperatorPolicy;
    private const string Staff = AuthenticationExtensions.PlatformStaffPolicy;

    // method pattern -> policy [+ permission]
    private static readonly Dictionary<string, string> Expected = new(StringComparer.Ordinal)
    {
        ["ANY /health/live"] = "anonymous",
        ["ANY /health/ready"] = "anonymous",
        // Authenticated by the provider's signature only (SPEC-0006 criterion 8), never by a token.
        ["POST /v1/payment-webhooks/{provider}"] = "anonymous",
        ["GET /v1/me"] = Tenant,
        ["GET /v1/me/memberships"] = Tenant,
        ["POST /v1/platform/tenants"] = Platform,
        ["GET /v1/platform/tenants/{id}"] = Platform,
        ["POST /v1/platform/tenants/{id}/suspend"] = Platform,
        ["POST /v1/platform/tenants/{id}/reactivate"] = Platform,
        ["POST /v1/platform/tenants/{id}/admins"] = Platform,
        ["GET /v1/platform/audit"] = Platform,
        ["POST /v1/platform/support/grants"] = Support,
        ["DELETE /v1/platform/support/grants/{grantId}"] = Staff,
        ["GET /v1/platform/support/tenants/{tenantId}/members"] = Support,
        ["GET /v1/platform/support/tenants/{tenantId}/jobs"] = Support,
        ["GET /v1/tenants/{tenantId}"] = $"{Tenant} + {TenantPermissions.TenantRead}",
        ["GET /v1/tenants/{tenantId}/members"] = $"{Tenant} + {TenantPermissions.MembersRead}",
        ["POST /v1/tenants/{tenantId}/members"] = $"{Tenant} + {TenantPermissions.MembersManage}",
        ["PUT /v1/tenants/{tenantId}/members/{membershipId}/role"] = $"{Tenant} + {TenantPermissions.MembersManage}",
        ["DELETE /v1/tenants/{tenantId}/members/{membershipId}"] = $"{Tenant} + {TenantPermissions.MembersManage}",
        ["GET /v1/tenants/{tenantId}/products"] = $"{Tenant} + {TenantPermissions.CatalogRead}",
        ["POST /v1/tenants/{tenantId}/products"] = $"{Tenant} + {TenantPermissions.CatalogManage}",
        ["GET /v1/tenants/{tenantId}/products/{productId}"] = $"{Tenant} + {TenantPermissions.CatalogRead}",
        ["PUT /v1/tenants/{tenantId}/products/{productId}"] = $"{Tenant} + {TenantPermissions.CatalogManage}",
        ["GET /v1/tenants/{tenantId}/products/{productId}/stock"] = $"{Tenant} + {TenantPermissions.InventoryRead}",
        ["POST /v1/tenants/{tenantId}/products/{productId}/stock/adjustments"] = $"{Tenant} + {TenantPermissions.InventoryManage}",
        ["POST /v1/tenants/{tenantId}/orders"] = $"{Tenant} + {TenantPermissions.OrdersPlace}",
        ["GET /v1/tenants/{tenantId}/orders/{orderId}"] = $"{Tenant} + {TenantPermissions.OrdersPlace}",
        ["PUT /v1/tenants/{tenantId}/orders/{orderId}/lines"] = $"{Tenant} + {TenantPermissions.OrdersPlace}",
        ["POST /v1/tenants/{tenantId}/orders/{orderId}/submit"] = $"{Tenant} + {TenantPermissions.OrdersPlace}",
        ["POST /v1/tenants/{tenantId}/orders/{orderId}/cancel"] = $"{Tenant} + {TenantPermissions.OrdersPlace}",
        ["POST /v1/tenants/{tenantId}/orders/{orderId}/payments"] = $"{Tenant} + {TenantPermissions.OrdersPlace}",
        ["GET /v1/tenants/{tenantId}/orders/{orderId}/payments/current"] = $"{Tenant} + {TenantPermissions.OrdersPlace}",
        ["POST /v1/tenants/{tenantId}/orders/{orderId}/payments/current/refunds"] = $"{Tenant} + {TenantPermissions.OrdersManage}",
        ["GET /v1/tenants/{tenantId}/orders/{orderId}/payments/current/refunds"] = $"{Tenant} + {TenantPermissions.OrdersManage}",
        ["POST /v1/tenants/{tenantId}/imports/products"] = $"{Tenant} + {TenantPermissions.CatalogManage}",
        ["GET /v1/tenants/{tenantId}/imports/{importId}"] = $"{Tenant} + {TenantPermissions.CatalogManage}",
        ["GET /v1/tenants/{tenantId}/imports/{importId}/errors"] = $"{Tenant} + {TenantPermissions.CatalogManage}",
        ["POST /v1/tenants/{tenantId}/exports/products"] = $"{Tenant} + {TenantPermissions.CatalogManage}",
        ["GET /v1/tenants/{tenantId}/exports/{exportId}"] = $"{Tenant} + {TenantPermissions.CatalogManage}",
        ["GET /v1/tenants/{tenantId}/exports/{exportId}/file"] = $"{Tenant} + {TenantPermissions.CatalogManage}",
        ["POST /v1/tenants/{tenantId}/plans"] = $"{Tenant} + {TenantPermissions.SubscriptionsManage}",
        ["GET /v1/tenants/{tenantId}/audit"] = $"{Tenant} + {TenantPermissions.AuditRead}",
        ["GET /v1/tenants/{tenantId}/plans"] = $"{Tenant} + {TenantPermissions.SubscriptionsSubscribe}",
        ["POST /v1/tenants/{tenantId}/subscriptions"] = $"{Tenant} + {TenantPermissions.SubscriptionsSubscribe}",
        ["GET /v1/tenants/{tenantId}/subscriptions/{subscriptionId}"] = $"{Tenant} + {TenantPermissions.SubscriptionsSubscribe}",
        ["POST /v1/tenants/{tenantId}/subscriptions/{subscriptionId}/cancel"] = $"{Tenant} + {TenantPermissions.SubscriptionsSubscribe}",
    };

    [Fact]
    public async Task The_route_table_is_exactly_the_reviewed_list_with_a_policy_on_every_route()
    {
        await using var factory = ApiFactory.CreateProduction();
        _ = factory.CreateClient();

        var actual = Describe(factory.Services.GetRequiredService<EndpointDataSource>());

        Assert.Equal(Expected.OrderBy(static pair => pair.Key, StringComparer.Ordinal), actual.OrderBy(static pair => pair.Key, StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_development_host_adds_only_the_anonymous_api_documentation_routes()
    {
        // The document and its reference UI describe routes, not data, and exist on a developer's machine only; the
        // Production table above (the one that ships) has neither.
        var documentation = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [$"GET {OpenApiExtensions.DocumentPath}"] = "anonymous",
            ["GET /scalar/{documentName?}"] = "anonymous",
            ["GET /scalar/scalar.js"] = "anonymous",
            ["GET /scalar/scalar.aspnetcore.js"] = "anonymous",
            ["GET /scalar/favicon.svg"] = "anonymous",
        };
        await using var factory = ApiFactory.CreateDevelopment();
        _ = factory.CreateClient();

        var actual = Describe(factory.Services.GetRequiredService<EndpointDataSource>());

        Assert.Equal(
            Expected.Concat(documentation).OrderBy(static pair => pair.Key, StringComparer.Ordinal),
            actual.OrderBy(static pair => pair.Key, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Only_health_probes_and_the_signed_webhook_are_anonymous_and_no_other_route_lacks_a_named_policy()
    {
        await using var factory = ApiFactory.CreateProduction();
        _ = factory.CreateClient();

        var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();

        Assert.NotEmpty(routes);
        foreach (var route in routes)
        {
            var anonymous = route.Metadata.GetMetadata<IAllowAnonymous>() is not null;
            var policies = route.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(static data => data.Policy).ToList();
            var isHealth = route.RoutePattern.RawText!.StartsWith("/health/", StringComparison.Ordinal);
            var isWebhook = route.RoutePattern.RawText == "/v1/payment-webhooks/{provider}";
            Assert.Equal(isHealth || isWebhook, anonymous);
            if (!isHealth && !isWebhook)
            {
                Assert.All(policies, policy => Assert.False(string.IsNullOrWhiteSpace(policy), $"{route.RoutePattern.RawText} uses the default policy"));
                Assert.NotEmpty(policies);
            }
        }
    }

    [Fact]
    public async Task Every_tenant_route_names_a_permission_that_exists_and_every_permission_is_required_somewhere()
    {
        await using var factory = ApiFactory.CreateProduction();
        _ = factory.CreateClient();
        // orders.manage guards the refund routes (SPEC-0007); the Orders module also checks it per order (SPEC-0004 Contract).
        var known = new[] { TenantPermissions.TenantRead, TenantPermissions.MembersRead, TenantPermissions.MembersManage, TenantPermissions.CatalogRead, TenantPermissions.CatalogManage, TenantPermissions.InventoryRead, TenantPermissions.InventoryManage, TenantPermissions.OrdersPlace, TenantPermissions.OrdersManage, TenantPermissions.SubscriptionsManage, TenantPermissions.SubscriptionsSubscribe, TenantPermissions.AuditRead };

        var required = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Select(static endpoint => endpoint.Metadata.GetMetadata<TenantPermissionRequirement>()?.Permission)
            .OfType<string>()
            .ToList();

        Assert.NotEmpty(required);
        Assert.All(required, permission => Assert.Contains(permission, known));
        Assert.Equal(known.Order(StringComparer.Ordinal), required.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
    }

    private static Dictionary<string, string> Describe(EndpointDataSource source)
    {
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var route in source.Endpoints.OfType<RouteEndpoint>())
        {
            // Health probes are mapped for any method: no method metadata.
            var methods = route.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["ANY"];
            var policies = route.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(static data => data.Policy ?? "(default)").ToList();
            var permission = route.Metadata.GetMetadata<TenantPermissionRequirement>()?.Permission;
            var description = route.Metadata.GetMetadata<IAllowAnonymous>() is not null
                ? "anonymous"
                : string.Join(", ", policies) + (permission is null ? string.Empty : $" + {permission}");
            foreach (var method in methods)
            {
                // A mapped group root is "/v1/me/" and the same route is "/v1/me": one route, so the trailing slash is not compared.
                table[$"{method} {route.RoutePattern.RawText!.TrimEnd('/')}"] = description;
            }
        }

        return table;
    }
}
