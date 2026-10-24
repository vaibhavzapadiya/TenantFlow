using System.Reflection;

namespace TenantFlow.Architecture.Tests;

/// <summary>
/// SPEC-0003 criterion 19 (ADR-0002, engineering conventions section 12). Read from what each compiled module assembly
/// actually references, so a stray <c>using</c> of another module's internals fails here even though it compiles.
/// </summary>
public sealed class ModuleBoundaryTests
{
    private static readonly string[] Modules = ["Audit", "Catalog", "IdentityAccess", "ImportsExports", "Inventory", "Notifications", "Operations", "Orders", "Payments", "Subscriptions", "Tenants"];

    // Which layers of its own module a layer may use. Contracts of any module and BuildingBlocks are checked separately.
    private static readonly Dictionary<string, string[]> OwnLayersAllowed = new(StringComparer.Ordinal)
    {
        ["Contracts"] = [],
        ["Domain"] = ["Contracts"],
        ["Application"] = ["Domain", "Contracts"],
        ["Infrastructure"] = ["Application", "Domain", "Contracts"],
    };

    [Fact]
    public void Every_module_has_the_expected_layers_and_no_other_module_assemblies_exist()
    {
        var found = ModuleAssemblies().Select(static a => (a.Module, a.Layer)).ToHashSet();

        // A new module or layer is added here deliberately, so these rules cover it.
        Assert.Equal(
            Modules.SelectMany(static m => OwnLayersAllowed.Keys.Select(l => (m, l)))
                // Notifications only consumes events; no module uses it yet, so no Contracts project (engineering conventions section 1).
                // Subscriptions is used by no module in M7 either (SPEC-0007), nor Operations in M9 (ADR-0027).
                .Where(static ml => ml != ("Notifications", "Contracts") && ml != ("Subscriptions", "Contracts") && ml != ("Operations", "Contracts"))
                .Order(),
            found.Order());
    }

    [Fact]
    public void A_module_uses_another_module_only_through_its_contracts()
    {
        var violations = ModuleAssemblies()
            .SelectMany(static a => a.Assembly.GetReferencedAssemblies()
                .Select(Describe)
                .OfType<(string Module, string Layer)>()
                .Where(r => r.Module != a.Module && r.Layer != "Contracts")
                .Select(r => $"{a.Module}.{a.Layer} -> {r.Module}.{r.Layer}"))
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void Dependencies_inside_a_module_point_inward()
    {
        var violations = ModuleAssemblies()
            .SelectMany(static a => a.Assembly.GetReferencedAssemblies()
                .Select(Describe)
                .OfType<(string Module, string Layer)>()
                .Where(r => r.Module == a.Module && !OwnLayersAllowed[a.Layer].Contains(r.Layer))
                .Select(r => $"{a.Module}.{a.Layer} -> {r.Module}.{r.Layer}"))
            .ToList();

        Assert.Empty(violations);
    }

    [Fact]
    public void Domain_application_and_contracts_depend_on_no_persistence_or_http_framework()
    {
        string[] forbidden = ["Microsoft.EntityFrameworkCore", "Npgsql", "Microsoft.AspNetCore", "TenantFlow.BuildingBlocks.Persistence"];
        var violations = ModuleAssemblies()
            .Where(static a => a.Layer != "Infrastructure")
            .SelectMany(a => a.Assembly.GetReferencedAssemblies()
                .Where(r => forbidden.Any(f => r.Name!.StartsWith(f, StringComparison.Ordinal)))
                .Select(r => $"{a.Module}.{a.Layer} -> {r.Name}"))
            .ToList();

        Assert.Empty(violations);
    }

    // "TenantFlow.Catalog.Domain" -> ("Catalog", "Domain"); anything that is not a module layer -> null.
    private static (string Module, string Layer)? Describe(AssemblyName name)
    {
        var parts = name.Name!.Split('.');
        return parts.Length == 3 && parts[0] == "TenantFlow" && Modules.Contains(parts[1]) && OwnLayersAllowed.ContainsKey(parts[2])
            ? (parts[1], parts[2])
            : null;
    }

    internal static IReadOnlyList<(string Module, string Layer, Assembly Assembly)> ModuleAssemblies()
    {
        // Both hosts together reference every module; walk their references to find every TenantFlow assembly.
        var seen = new Dictionary<string, Assembly>(StringComparer.Ordinal);
        var pending = new Stack<Assembly>([typeof(Program).Assembly, typeof(TenantFlow.Migrator.MigratorApplication).Assembly]);
        while (pending.TryPop(out var assembly))
        {
            foreach (var reference in assembly.GetReferencedAssemblies().Where(static r => r.Name!.StartsWith("TenantFlow.", StringComparison.Ordinal)))
            {
                if (!seen.ContainsKey(reference.Name!))
                {
                    var loaded = Assembly.Load(reference);
                    seen[reference.Name!] = loaded;
                    pending.Push(loaded);
                }
            }
        }

        return [.. seen.Values
            .Select(static a => (Description: Describe(a.GetName()), Assembly: a))
            .Where(static x => x.Description is not null)
            .Select(static x => (x.Description!.Value.Module, x.Description!.Value.Layer, x.Assembly))];
    }
}
