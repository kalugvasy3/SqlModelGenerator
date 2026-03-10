using System.Text;
using System.Text.RegularExpressions;

namespace SqlModelGenerator.Metadata;

public static class NameHelper
{
    private static readonly Regex NonLetterOrDigit = new("[^a-zA-Z0-9]+", RegexOptions.Compiled);

    public static string ToPascalCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "Unnamed";

        value = value.Trim();
        value = value.Replace("[", string.Empty).Replace("]", string.Empty);

        var tokens = NonLetterOrDigit.Split(value)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        if (tokens.Count == 0)
            return "Unnamed";

        var sb = new StringBuilder();
        foreach (var token in tokens)
        {
            var t = token;
            if (char.IsDigit(t[0]))
                sb.Append('_');

            sb.Append(char.ToUpperInvariant(t[0]));
            if (t.Length > 1)
                sb.Append(t.Substring(1));
        }

        return sb.ToString();
    }

    public static string ToCamelCase(string value)
    {
        var pascal = ToPascalCase(value);
        if (string.IsNullOrEmpty(pascal))
            return "value";

        if (pascal.Length == 1)
            return pascal.ToLowerInvariant();

        return char.ToLowerInvariant(pascal[0]) + pascal.Substring(1);
    }

    public static string EscapeIdentifier(string value)
    {
        var keywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "class", "namespace", "event", "string", "int", "decimal", "params", "base",
            "object", "internal", "public", "private", "protected", "operator", "ref",
            "out", "in", "default", "return", "void", "new", "null", "record"
        };

        return keywords.Contains(value) ? "@" + value : value;
    }
}
