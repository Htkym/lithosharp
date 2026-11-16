using LithoSharp.Content;
using Syntamark.Compilation;

namespace LithoSharp.Inspection;

internal sealed record MarkdownComponentCompletion(string Label, string InsertText, string Detail, SourceSpan Span, bool Deprecated = false)
{
    internal int Start => Span.Start;
    internal int Length => Span.Length;
}
internal sealed record MarkdownComponentHover(string Contents, SourceSpan Span)
{
    internal int Start => Span.Start;
    internal int Length => Span.Length;
}

// Reads retained native syntax and generated metadata only; it never calls a component or binder.
internal static class MarkdownComponentEditing
{
    internal static IReadOnlyList<MarkdownComponentCompletion> Complete(DocumentInfo document, int offset,
        MarkdownComponentRegistry registry, CancellationToken cancellationToken = default)
        => Complete(document, offset, registry.Schemas, cancellationToken);

    internal static IReadOnlyList<MarkdownComponentCompletion> Complete(DocumentInfo document, int offset,
        IReadOnlyList<MarkdownComponentSchema> schemas, CancellationToken cancellationToken = default)
    {
        if (Find(document, offset, cancellationToken) is not { } directive)
            return [];
        if (directive.Attributes.Count(static attribute => attribute.Name == "name") > 1)
            return [];
        var local = offset - document.BodyStartOffset;
        var attribute = directive.Attributes.FirstOrDefault(attribute => Contains(attribute.RawSpan, local));
        if (attribute is { Name: "name" } && (attribute.ValueRawSpan is { } selectorValue && Contains(selectorValue, local)
            || attribute.ValueRawSpan is null && local >= attribute.NameRawSpan.End))
            return schemas.Select(registration => new MarkdownComponentCompletion(registration.Name, registration.Name,
                "Static Markdown component", attribute.ValueRawSpan is { } span ? Shift(document, span) : new SourceSpan(offset, 0))).ToArray();
        var selector = directive.Attributes.SingleOrDefault(static attribute => attribute.Name == "name");
        var registration = schemas.FirstOrDefault(schema => schema.Name == selector?.Value?.Text);
        if (registration is null)
            return attribute?.Name == "name" ? [] : [new MarkdownComponentCompletion("name", "name=\"\"", "Registered component selector",
                attribute is null ? new SourceSpan(offset, 0) : Shift(document, attribute.NameRawSpan))];
        var fields = Fields(registration);
        if (attribute?.ValueRawSpan is { } value && Contains(value, local))
        {
            var field = fields.FirstOrDefault(field => field.Key == attribute.Name);
            var values = field?.EnumValues ?? [];
            if (field?.Type == "boolean")
                values = ["true", "false"];
            return values.Select(choice => new MarkdownComponentCompletion(choice, choice, field!.Type, Shift(document, value), field.Deprecated)).ToArray();
        }
        var present = directive.Attributes.Where(current => current != attribute).Select(static current => current.Name).ToHashSet(StringComparer.Ordinal);
        return fields.Where(field => field.Key != "name" && !present.Contains(field.Key)).Select(field =>
            new MarkdownComponentCompletion(field.Key, attribute is null ? field.Key + "=\"\"" : field.Key,
                Describe(field), attribute is null ? new SourceSpan(offset, 0) : Shift(document, attribute.NameRawSpan), field.Deprecated)).ToArray();
    }

    internal static MarkdownComponentHover? Hover(DocumentInfo document, int offset, MarkdownComponentRegistry registry,
        CancellationToken cancellationToken = default)
        => Hover(document, offset, registry.Schemas, cancellationToken);

    internal static MarkdownComponentHover? Hover(DocumentInfo document, int offset, IReadOnlyList<MarkdownComponentSchema> schemas,
        CancellationToken cancellationToken = default)
    {
        if (Find(document, offset, cancellationToken) is not { } directive)
            return null;
        if (directive.Attributes.Count(static attribute => attribute.Name == "name") > 1)
            return null;
        var local = offset - document.BodyStartOffset;
        var attribute = directive.Attributes.FirstOrDefault(attribute => Contains(attribute.RawSpan, local));
        var selector = directive.Attributes.FirstOrDefault(static attribute => attribute.Name == "name");
        var registration = schemas.FirstOrDefault(schema => schema.Name == selector?.Value?.Text);
        if (registration is null)
            return null;
        if (attribute?.Name == "name")
            return new MarkdownComponentHover($"Static component `{registration.Name}`. Props use literal attribute values. Runtime binding still requires a build.",
                Shift(document, attribute.RawSpan));
        var field = Fields(registration).FirstOrDefault(field => field.Key == attribute?.Name);
        return field is null || attribute is null ? null : new MarkdownComponentHover(Describe(field), Shift(document, attribute.RawSpan));
    }

    private static MdDirectiveSyntax? Find(DocumentInfo document, int offset, CancellationToken cancellationToken)
    {
        if (document.MarkdownSyntax is not { ProfileId: MarkdownOptions.DocumentationProfile, Root: { } root })
            return null;
        var local = offset - document.BodyStartOffset;
        return root.DescendantsAndSelf(cancellationToken).Select(static node => node.Directive)
            .FirstOrDefault(directive => directive is { Name: "component" } && Contains(directive.HeaderRawSpan, local));
    }

    private static IReadOnlyList<FrontMatterFieldInfo> Fields(MarkdownComponentSchema registration)
    {
        if (registration.SchemaJson is null)
            return [];
        try
        {
            return GeneratedFrontMatterSchema.Parse(registration.SchemaJson).Fields;
        }
        catch (ArgumentException)
        {
            return [];
        }
    }

    private static string Describe(FrontMatterFieldInfo field) => $"{field.Key}: {field.Type}"
        + (field.Required ? " (required)" : string.Empty)
        + (field.Deprecated ? " (deprecated)" : string.Empty)
        + (field.Description is { Length: > 0 } description ? "\n\n" + description : string.Empty)
        + (field.RequiresBuildValidation ? "\n\nDefaults and member code require build validation." : string.Empty);

    private static bool Contains(MdRawRange span, int offset) => offset >= span.Start && offset <= span.End;
    private static SourceSpan Shift(DocumentInfo document, MdRawRange span) => new(document.BodyStartOffset + span.Start, span.Length);
}
