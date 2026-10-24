using Microsoft.EntityFrameworkCore;

namespace TenantFlow.Architecture.Tests;

/// <summary>
/// SPEC-0003 criterion 19 (ADR-0012): each module has one DbContext, and it maps every entity to that module's own
/// schema. Building the model needs no database: the connection string names no server.
/// </summary>
public sealed class ModuleSchemaTests
{
    [Fact]
    public void Each_module_dbcontext_maps_only_its_own_schema()
    {
        var contexts = ModuleBoundaryTests.ModuleAssemblies()
            .Where(static a => a.Layer == "Infrastructure")
            .SelectMany(static a => a.Assembly.GetTypes()
                .Where(static t => !t.IsAbstract && typeof(DbContext).IsAssignableFrom(t))
                .Select(t => (a.Module, Type: t)))
            .ToList();

        Assert.Equal(["Audit", "Catalog", "IdentityAccess", "ImportsExports", "Inventory", "Notifications", "Operations", "Orders", "Payments", "Subscriptions", "Tenants"], contexts.Select(static c => c.Module).Order());
        foreach (var (module, type) in contexts)
        {
            var schema = (string)type.GetField("Schema")!.GetValue(null)!;
            using var context = Create(type);
            var mapped = context.Model.GetEntityTypes().Select(static e => e.GetSchema()).Distinct().ToList();

            Assert.True(mapped.SequenceEqual([schema]), $"{module}: {type.Name} maps schemas [{string.Join(", ", mapped)}], expected [{schema}]");
        }
    }

    [Fact]
    public void Each_module_has_a_migration_for_every_change_to_its_model()
    {
        // Catches a model change without its migration, such as a Catalog contract limit widened (ProductLimits) while
        // the Orders snapshot columns sized from it keep their old width in the database.
        var stale = ModuleBoundaryTests.ModuleAssemblies()
            .Where(static a => a.Layer == "Infrastructure")
            .SelectMany(static a => a.Assembly.GetTypes().Where(static t => !t.IsAbstract && typeof(DbContext).IsAssignableFrom(t)))
            .Where(static type =>
            {
                using var context = Create(type);
                return context.Database.HasPendingModelChanges();
            })
            .Select(static type => type.Name)
            .ToList();

        Assert.Empty(stale);
    }

    private static DbContext Create(Type contextType)
    {
        var optionsType = typeof(DbContextOptionsBuilder<>).MakeGenericType(contextType);
        var builder = (DbContextOptionsBuilder)Activator.CreateInstance(optionsType)!;
        builder.UseNpgsql("Host=architecture-test-only");
        return (DbContext)Activator.CreateInstance(contextType, builder.Options)!;
    }
}
