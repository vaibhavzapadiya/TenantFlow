namespace TenantFlow.BuildingBlocks.Abstractions.Text;

/// <summary>
/// The one definition of characters that stored, human-readable text must not contain: control characters and the
/// Unicode text-direction overrides and isolates, which can make a name display differently from what it is.
/// Every module's name and reason rules use it, so a new dangerous range is added in one place.
/// </summary>
public static class TextRules
{
    public static bool ContainsDisallowedCharacters(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Any(IsDisallowed);
    }

    private static bool IsDisallowed(char c) =>
        char.IsControl(c) || c is (>= '‪' and <= '‮') or (>= '⁦' and <= '⁩');
}
