using System;
using System.Text;

namespace LithoSharp.Content.Compilation;

/// <summary>Parser limits shared by runtime and source hosts.</summary>
internal static partial class LithoLimits
{
    /// <summary>Maximum nested container depth (blockquotes/lists). Deeper input degrades to paragraphs.</summary>
    public const int MaxNestingDepth = 200;

    /// <summary>Maximum reference label length, per CommonMark.</summary>
    public const int MaxReferenceLabelLength = 999;

    /// <summary>Replaces unpaired surrogates with U+FFFD so normalization never throws on document text.</summary>
    internal static string SanitizeUnpairedSurrogates(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if ((char.IsHighSurrogate(ch) && (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1])))
                || (char.IsLowSurrogate(ch) && (i == 0 || !char.IsHighSurrogate(text[i - 1]))))
            {
                var builder = new StringBuilder(text.Length + 8);
                builder.Append(text, 0, i);
                for (; i < text.Length; i++)
                {
                    ch = text[i];
                    builder.Append((char.IsHighSurrogate(ch) && (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1])))
                        || (char.IsLowSurrogate(ch) && (i == 0 || !char.IsHighSurrogate(text[i - 1])))
                        ? '\uFFFD' : ch);
                }

                return builder.ToString();
            }
        }

        return text;
    }

    /// <summary>Lowercases with diacritic removal for slug comparison helpers.</summary>
    public static string RemoveDiacritics(string text)
    {
        var normalized = SanitizeUnpairedSurrogates(text).Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch)
                != System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }
}
