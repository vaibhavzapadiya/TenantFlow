using System.Text.RegularExpressions;

namespace TenantFlow.BuildingBlocks.Messaging;

/// <summary>The schema guard of the stores whose SQL text names a module's schema.</summary>
internal static partial class SqlSchema
{
    /// <summary>
    /// Returns <paramref name="schema"/> when it is a plain lower-case identifier; anything else is refused, because the
    /// schema becomes part of the SQL text.
    /// </summary>
    public static string Ensure(string? schema, string paramName, string what) =>
        schema is not null && Identifier().IsMatch(schema)
            ? schema
            : throw new ArgumentException($"The {what} schema must be a lower-case identifier.", paramName);

    [GeneratedRegex("^[a-z][a-z_]{0,62}$")]
    private static partial Regex Identifier();
}
