using System;
using System.Collections.Generic;
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
        if (text is null) throw new ArgumentNullException(nameof(text));
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
            match.Value.IndexOf('.') >= 0 ? "." : "-");
        collapsed = collapsed.Trim('-', '.');
        collapsed = LeadingDigits().Replace(collapsed, string.Empty);
        return collapsed.Length == 0 ? "section" : collapsed;
    }

    /// <summary>Assigns unique slugs across a document, suffixing duplicates with -1, -2, ....</summary>
    public static IReadOnlyList<string> Assign(IReadOnlyList<string> texts)
    {
        if (texts is null) throw new ArgumentNullException(nameof(texts));
        var allocator = new LithoAnchorIds();
        var result = new string[texts.Count];
        for (var index = 0; index < texts.Count; index++) result[index] = allocator.Next(Slugify(texts[index]));

        return result;
    }

    #if NETSTANDARD2_0
    private static readonly Regex HyphenDotRunRegex = new(@"[-.]+");
    private static Regex HyphenDotRun() => HyphenDotRunRegex;
    private static readonly Regex LeadingDigitsRegex = new(@"^[0-9]+");
    private static Regex LeadingDigits() => LeadingDigitsRegex;
    #else
    [GeneratedRegex(@"[-.]+")]
    private static partial Regex HyphenDotRun();

    [GeneratedRegex(@"^[0-9]+")]
    private static partial Regex LeadingDigits();
    #endif
}

/// <summary>Remembers the next suffix for each base while preserving first-unused collision ordering.</summary>
internal sealed class LithoAnchorIds(MdParseContext? context = null)
{
    private readonly HashSet<string> used = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> nextSuffix = new(StringComparer.Ordinal);
    internal string Next(string basis)
    {
        context?.Scan((long)basis.Length * 2);
        if (used.Add(basis)) return basis;
        var suffix = nextSuffix.TryGetValue(basis, out var next) ? next : 1;
        while (true)
        {
            // Decimal int needs at most ten UTF-16 units; charge before constructing/hashing a candidate.
            context?.Scan(((long)basis.Length + 11) * 2);
            var candidate = basis + "-" + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
            suffix = checked(suffix + 1);
            if (!used.Add(candidate)) continue;
            nextSuffix[basis] = suffix;
            return candidate;
        }
    }
}
