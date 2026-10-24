using System.Reflection;

namespace TenantFlow.Architecture.Tests;

/// <summary>
/// ADR-0021 decision 7 (SPEC-0005 open question 1): the outbox is not under row-level security, so only the store that
/// writes it in the state change's transaction, and the module's migrations, may name it. The publisher lives in
/// BuildingBlocks, outside every module. Any other module or host code that names <c>outbox_messages</c> in SQL fails here.
/// The same holds for <c>workflow_timers</c> (ADR-0022 decision 7), also outside RLS and read only by the timer runner.
/// </summary>
public sealed class OutboxAccessTests
{
    private static readonly string[] Tables = ["outbox_messages", "workflow_timers"];
    private const byte LoadString = 0x72;
    private const int UserStringTable = 0x70;

    [Fact]
    public void Only_the_owning_store_and_migrations_name_the_outbox_table()
    {
        var assemblies = ModuleBoundaryTests.ModuleAssemblies().Select(static a => a.Assembly).Append(typeof(Program).Assembly);

        var offenders = assemblies
            .SelectMany(static a => a.GetTypes())
            .Where(static t => !IsAllowed(t) && NamesOutbox(t))
            .Select(static t => t.FullName)
            .ToList();

        Assert.Empty(offenders);
    }

    // Each publishing module's store (including the state machines of its async methods) and its migrations.
    private static bool IsAllowed(Type type)
    {
        var outermost = type;
        while (outermost.DeclaringType is not null)
        {
            outermost = outermost.DeclaringType;
        }

        // ASP.NET Core 10's OpenAPI source generator caches the XML doc comments of the API's types. That cache holds
        // documentation text, so a comment describing a table is not SQL against it.
        if (outermost.Namespace is "Microsoft.AspNetCore.OpenApi.Generated")
        {
            return true;
        }

        // A module's retention rules may name its timers: every rule is scoped to one tenant (RetentionRule.Delete adds
        // tenant_id = @tenant) and runs inside that tenant's transaction (ADR-0025 decision 4).
        if (outermost.Namespace is { } ns && ns.StartsWith("TenantFlow.", StringComparison.Ordinal) && ns.EndsWith(".Infrastructure", StringComparison.Ordinal)
            && outermost.Name == ns["TenantFlow.".Length..^".Infrastructure".Length] + "Retention")
        {
            return true;
        }

        return outermost.FullName is "TenantFlow.Orders.Infrastructure.Persistence.OrderStore" or "TenantFlow.Payments.Infrastructure.Persistence.PaymentStore"
            || outermost.Namespace is "TenantFlow.Orders.Infrastructure.Persistence.Migrations" or "TenantFlow.Payments.Infrastructure.Persistence.Migrations"
                or "TenantFlow.Subscriptions.Infrastructure.Persistence.Migrations" or "TenantFlow.ImportsExports.Infrastructure.Persistence.Migrations"
                // Tenants and IdentityAccess publish audit events (ADR-0026); their stores add rows through EF Core only.
                or "TenantFlow.Tenants.Infrastructure.Persistence.Migrations" or "TenantFlow.IdentityAccess.Infrastructure.Persistence.Migrations"
                or "TenantFlow.Operations.Infrastructure.Persistence.Migrations";
    }

    // True when any method body of the type loads a string literal that contains the table name.
    private static bool NamesOutbox(Type type)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        IEnumerable<MethodBase> methods = [.. type.GetMethods(all), .. type.GetConstructors(all)];
        return methods.Any(method => method.GetMethodBody()?.GetILAsByteArray() is { } il && LoadsOutboxLiteral(method.Module, il));
    }

    private static bool LoadsOutboxLiteral(Module module, byte[] il)
    {
        for (var i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] != LoadString || il[i + 4] != UserStringTable)
            {
                continue;
            }

            try
            {
                if (module.ResolveString(BitConverter.ToInt32(il, i + 1)) is var literal && Tables.Any(table => literal.Contains(table, StringComparison.Ordinal)))
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                // Operand bytes that only looked like a string load.
            }
        }

        return false;
    }
}
