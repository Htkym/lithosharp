using LithoSharp.HtmlParsing;

namespace LithoSharp.Quality;

// Local observations only: no resolved URLs, diagnostics, trees or build cancellation tokens.
// Resolution and site-wide rules run again against the current artifact/anchor graph.
internal sealed record QualityHtmlFacts(string Title, string? BaseHref, string[] Anchors,
    QualityHtmlElement[] Elements)
{
    internal static QualityHtmlFacts Parse(string source, CancellationToken cancellationToken)
    {
        var facts = HtmlFacts.Parse(source, cancellationToken);
        var elements = new List<QualityHtmlElement>();
        foreach (var element in facts.Elements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var position = facts.Position(element);
            elements.Add(new(element.Name!, element.Attributes.Select(attribute =>
                new QualityHtmlAttribute(attribute.Name, attribute.Namespace, attribute.Value.Value)).ToArray(),
                position?.Line, position?.Column, element.Name == "style" ? facts.TextContent(element.Id) : null));
        }
        return new(facts.Title, facts.BaseHref, facts.Anchors.ToArray(), elements.ToArray());
    }

    internal static string? Attribute(QualityHtmlElement element, string name) =>
        element.Attributes.FirstOrDefault(attribute => attribute.Namespace is null && attribute.Name == name)?.Value;

    internal bool IsValid() => Title is not null && Anchors is not null
        && Anchors.All(anchor => !string.IsNullOrEmpty(anchor)) && Elements is { Length: <= 250_000 }
        && Elements.All(element => element is not null && !string.IsNullOrEmpty(element.Name)
            && element.Attributes is { Length: <= 256 } && element.Attributes.All(attribute =>
                attribute is not null && !string.IsNullOrEmpty(attribute.Name) && attribute.Value is not null)
            && ((element.Line is null && element.Column is null) || (element.Line > 0 && element.Column > 0))
            && (element.Name != "style" || element.StyleText is not null));

    // A conservative JSON upper bound avoids serializing very large projections for admission.
    internal long MaximumSerializedBytes => 2048L + 6L * (Title.Length + (BaseHref?.Length ?? 0)
        + Anchors.Sum(anchor => (long)anchor.Length)) + Anchors.Length * 8L
        + Elements.Sum(element => 256L + 6L * (element.Name.Length + (element.StyleText?.Length ?? 0))
            + element.Attributes.Sum(attribute => 96L + 6L * (attribute.Name.Length
                + (attribute.Namespace?.Length ?? 0) + attribute.Value.Length)));
}

internal sealed record QualityHtmlElement(string Name, QualityHtmlAttribute[] Attributes,
    int? Line, int? Column, string? StyleText);
internal sealed record QualityHtmlAttribute(string Name, string? Namespace, string Value);
