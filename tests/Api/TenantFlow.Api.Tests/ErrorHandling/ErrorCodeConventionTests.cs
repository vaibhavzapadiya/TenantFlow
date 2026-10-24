using System.Reflection;
using System.Text.RegularExpressions;
using TenantFlow.BuildingBlocks.Abstractions.Errors;
using TenantFlow.IdentityAccess.Domain.Memberships;
using TenantFlow.Tenants.Domain.Tenants;

namespace TenantFlow.Api.Tests.ErrorHandling;

/// <summary>
/// ADR-0016: error codes are a public contract, so every catalog entry follows one convention and no two
/// entries share a code. New modules' catalogs are picked up automatically by their assembly name.
/// </summary>
public sealed class ErrorCodeConventionTests
{
    private static readonly Regex CodePattern = new(AppError.CodePattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    [Fact]
    public void Every_catalog_entry_follows_the_code_convention_and_codes_are_unique()
    {
        // An error shared through a module contract appears in two catalogs but is one entry.
        var errors = CatalogErrors().Distinct().ToList();

        Assert.NotEmpty(errors);
        Assert.All(errors, error =>
        {
            Assert.Matches(CodePattern, error.Code);
            Assert.False(string.IsNullOrWhiteSpace(error.Message), $"{error.Code} has no message");
            // Messages are fixed sentences: no format placeholders, no stack traces, no tenant-specific data.
            Assert.DoesNotContain("{", error.Message, StringComparison.Ordinal);
            Assert.EndsWith(".", error.Message, StringComparison.Ordinal);
        });
        Assert.Equal(errors.Count, errors.Select(static error => error.Code).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Validation_entries_are_the_only_ones_with_the_validation_kind_and_conflicts_use_conflict()
    {
        var byCode = CatalogErrors().Distinct().ToDictionary(static error => error.Code, StringComparer.Ordinal);

        Assert.Equal(ErrorKind.NotFound, byCode["tenant.not_found"].Kind);
        Assert.Equal(ErrorKind.Conflict, byCode["tenant.already_suspended"].Kind);
        Assert.Equal(ErrorKind.Conflict, byCode["tenant.not_suspended"].Kind);
        Assert.Equal(ErrorKind.Conflict, byCode["tenant.concurrent_update"].Kind);
        Assert.Equal(ErrorKind.Conflict, byCode["tenant.name_taken"].Kind);
        Assert.Equal(ErrorKind.Validation, byCode["tenant.name.required"].Kind);
        Assert.Equal(ErrorKind.Validation, byCode["tenant.name.too_long"].Kind);
    }

    private static IEnumerable<AppError> CatalogErrors()
    {
        // Referencing a type makes sure its assembly is loaded; further modules add a line here.
        _ = typeof(TenantErrors);
        _ = typeof(MembershipErrors);
        return AppDomain.CurrentDomain.GetAssemblies()
            .Where(static assembly => assembly.GetName().Name?.StartsWith("TenantFlow.", StringComparison.Ordinal) == true)
            .SelectMany(static assembly => assembly.GetTypes())
            .Where(static type => type is { IsAbstract: true, IsSealed: true } && type.Name.EndsWith("Errors", StringComparison.Ordinal))
            .SelectMany(static type => type.GetFields(BindingFlags.Public | BindingFlags.Static))
            .Where(static field => field.FieldType == typeof(AppError))
            .Select(static field => (AppError)field.GetValue(null)!);
    }
}
