using System.Text.RegularExpressions;

namespace LithoSharp.Content.Compilation;

/// <summary>
/// Heading auto-identifier matching the Markdig output observed on the C00 baseline
/// (lowercase, diacritics removed, kept [a-z0-9 _.-], whitespace runs to one hyphen,
/// [-.]+ runs prefer ".", trimmed, leading digits stripped, empty falls back).
/// </summary>
internal static partial class LithoSlug
{
    /// <summary>Creates a base slug for heading text (without duplicate suffixing).</summary>
    public static string Slugify(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lowered = LithoLimits.RemoveDiacritics(text.ToLowerInvariant());
        var builder = new System.Text.StringBuilder(lowered.Length);
        foreach (var ch in lowered)
        {
            if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch is '_' or '.' or '-' || char.IsWhiteSpace(ch))
            {
                builder.Append(char.IsWhiteSpace(ch) ? '-' : ch);
            }
        }

        var collapsed = HyphenDotRun().Replace(builder.ToString(), static match =>
            match.Value.Contains('.', StringComparison.Ordinal) ? "." : "-");
        collapsed = collapsed.Trim('-', '.');
        collapsed = LeadingDigits().Replace(collapsed, string.Empty);
        return collapsed.Length == 0 ? "section" : collapsed;
    }

    /// <summary>Assigns unique slugs across a document, suffixing duplicates with -1, -2, ....</summary>
    public static IReadOnlyList<string> Assign(IReadOnlyList<string> texts)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var result = new string[texts.Count];
        for (var index = 0; index < texts.Count; index++)
        {
            var candidate = Slugify(texts[index]);
            if (!used.Add(candidate))
            {
                var suffix = 1;
                while (!used.Add($"{candidate}-{suffix}"))
                {
                    suffix++;
                }

                candidate = $"{candidate}-{suffix}";
            }

            result[index] = candidate;
        }

        return result;
    }

    [GeneratedRegex(@"[-.]+")]
    private static partial Regex HyphenDotRun();

    [GeneratedRegex(@"^[0-9]+")]
    private static partial Regex LeadingDigits();
}
