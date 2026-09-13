namespace LithoSharp.Content.Compilation;

/// <summary>
/// Directive names shared between the Markdown and MDX frontends. The MDX worker
/// accepts note, tip, info, warning, danger, and caution for <c>:::name</c>;
/// GitHub alerts add important. The Litho frontend unifies both syntaxes on the
/// seven names below (case-insensitive for <c>[!NAME]</c>, exact lowercase for
/// <c>:::name</c>, matching each reference). Unknown <c>:::name</c> containers
/// render as plain divs (Markdig parity) so content is never silently dropped.
/// </summary>
internal static class LithoDirectives
{
    /// <summary>Admonition kinds, in canonical order.</summary>
    public static IReadOnlyList<string> AdmonitionKinds { get; } =
    [
        "note",
        "tip",
        "info",
        "warning",
        "danger",
        "caution",
        "important",
    ];

    /// <summary>Whether the name is a <c>:::name</c> admonition (exact lowercase match).</summary>
    public static bool IsAdmonitionName(string name) =>
        AdmonitionKinds.Contains(name, StringComparer.Ordinal);

    /// <summary>Parses a <c>[!NAME]</c> alert tag (case-insensitive) to its lowercase kind.</summary>
    public static bool TryParseAlertName(string name, out string kind)
    {
        foreach (var candidate in AdmonitionKinds)
        {
            if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase))
            {
                kind = candidate;
                return true;
            }
        }

        kind = string.Empty;
        return false;
    }

    /// <summary>Default admonition title: the kind name (MDX runtime parity).</summary>
    public static string DefaultTitle(string kind) => kind;
}
