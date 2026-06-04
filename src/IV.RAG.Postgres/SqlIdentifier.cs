using System.Text.RegularExpressions;

namespace IV.RAG;

// Table names are interpolated directly into SQL (they cannot be passed as query parameters), so
// they are validated against a strict identifier pattern to close any injection vector when a name
// originates from configuration or user input.
internal static class SqlIdentifier
{
    private static readonly Regex Safe =
        new(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)?$", RegexOptions.Compiled);

    /// <summary>
    /// Returns <paramref name="identifier"/> if it is a safe SQL identifier (letters, digits, and
    /// underscores, not starting with a digit, optionally qualified as <c>schema.table</c>);
    /// otherwise throws <see cref="ArgumentException"/>.
    /// </summary>
    internal static string Validate(string identifier, string paramName)
    {
        if (string.IsNullOrEmpty(identifier) || !Safe.IsMatch(identifier))
            throw new ArgumentException(
                $"Table name '{identifier}' is invalid. Use only letters, digits, and underscores " +
                $"(not starting with a digit), optionally qualified as 'schema.table'.",
                paramName);
        return identifier;
    }
}
