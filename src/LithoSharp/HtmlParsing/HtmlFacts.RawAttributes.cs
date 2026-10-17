namespace LithoSharp.HtmlParsing;

// Source-only entry: no caller can supply an unrelated tree to the raw capture boundary.
internal sealed partial class HtmlFacts
{
    internal static (HtmlFacts Facts, HtmlRawAttributeFacts RawAttributes) ParseWithRawAttributes(
        string source, CancellationToken cancellationToken = default, HtmlTreeOptions? options = null,
        bool includeSearchMaterials = false)
    {
        // Existing Parse constructs HtmlFacts privately from this exact source and its one resulting tree.
        var parsed = Parse(source, cancellationToken, options);
        var raw = CaptureRawAttributesCore(source, parsed.tree, cancellationToken);
        return (parsed, raw.WithMaterials(parsed.CaptureTextMaterialsCore(source, includeSearchMaterials, cancellationToken)));
    }

    private static HtmlRawAttributeFacts CaptureRawAttributesCore(string source, CompactHtmlTree tree, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Parse already throws for Partial/Failed. This producer handles mapping coverage only.
        var status = HtmlFactsCoverageStatus.Complete;
        var reasons = new List<HtmlFactsCoverageReason>();

        var elements = new List<HtmlRawElementSnapshot>();
        if (status == HtmlFactsCoverageStatus.Complete)
        {
            // Repaired active-tree order and surviving attributes, not original token order.
            // TemplateContent is separate; removed duplicate attributes are not reconstructed.
            foreach (var element in tree.Elements())
            {
                token.ThrowIfCancellationRequested();
                var attributes = new List<HtmlRawAttributeSnapshot>();
                foreach (var attribute in element.Attributes)
                {
                    token.ThrowIfCancellationRequested();
                    if (!HtmlRawValueSnapshot.TryCapture(source, attribute.RawName, token, out var name, out var nameReason))
                    {
                        reasons.Add(nameReason!);
                        status = HtmlFactsCoverageStatus.Partial;
                        continue;
                    }
                    if (!HtmlRawValueSnapshot.TryCapture(source, attribute.Value, token, out var value, out var valueReason))
                    {
                        reasons.Add(valueReason!);
                        status = HtmlFactsCoverageStatus.Partial;
                        continue;
                    }
                    attributes.Add(new(attribute.Name, attribute.Namespace, attribute.Prefix, name!, value!));
                }
                elements.Add(new(elements.Count, element.Name!, element.Namespace!, element.Source, attributes));
            }
        }
        token.ThrowIfCancellationRequested();
        return new(elements, Enum.GetValues<HtmlFactsProjection>().Select(projection =>
            projection == HtmlFactsProjection.RawAttributes
                ? new HtmlFactsProjectionCoverage(projection, status, reasons)
                : new HtmlFactsProjectionCoverage(projection, HtmlFactsCoverageStatus.NotRequested, [])));
    }
}
