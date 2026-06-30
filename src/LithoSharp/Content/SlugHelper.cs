using System.Text;
using System.Text.RegularExpressions;

namespace LithoSharp.Content;

/// <summary>
/// A helper that builds a safe slug from a file name or title.
/// </summary>
public static partial class SlugHelper
{
    /// <summary>Converts the input string into a slug suitable for a URL path.</summary>
    /// <param name="value">The string to convert.</param>
    /// <returns>The slug.</returns>
    public static string ToSlug(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var normalized = value.Normalize(NormalizationForm.FormKD).ToLowerInvariant();
        var replaced = UnsafeSlugCharacters().Replace(normalized, "-").Trim('-');
        var collapsed = RepeatedDash().Replace(replaced, "-");
        return string.IsNullOrWhiteSpace(collapsed) ? "post" : collapsed;
    }

    [GeneratedRegex(@"[^a-z0-9\p{IsHiragana}\p{IsKatakana}\p{IsCJKUnifiedIdeographs}]+", RegexOptions.Compiled)]
    private static partial Regex UnsafeSlugCharacters();

    [GeneratedRegex("-{2,}", RegexOptions.Compiled)]
    private static partial Regex RepeatedDash();
}
