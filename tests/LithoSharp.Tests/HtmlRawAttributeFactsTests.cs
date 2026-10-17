using ParsedHtmlText = global::LithoSharp.HtmlParsing.HtmlText;
using LithoSharp.HtmlParsing;

namespace LithoSharp.Tests;

public sealed class HtmlRawAttributeFactsTests
{
    [Test]
    public async Task RawAttributesKeepUtf16MappingsNamespacesAndSurvivingActiveTreeOrder()
    {
        const string source = "<!doctype html><body><p id='first' id='lost' DATA-X='A\r\n&amp;&#x1F600;\uD800' empty='' disabled></p>"
            + "<template><p id=inert>Hidden</p></template><svg VIEWBOX='0 0' xlink:href='&amp;'></svg></body>";
        var facts = CaptureParsed(source);
        var coverage = facts.Coverage.Single(c => c.Projection == HtmlFactsProjection.RawAttributes);
        await Assert.That(coverage.Status).IsEqualTo(HtmlFactsCoverageStatus.Complete);
        await Assert.That(coverage.Reasons.Count).IsEqualTo(0);
        await Assert.That(facts.Elements.Select(e => e.Ordinal).SequenceEqual(Enumerable.Range(0, facts.Elements.Count))).IsTrue();
        var paragraph = facts.Elements.Single(e => e.Name == "p");
        await Assert.That(paragraph.Attributes.Select(a => a.Name).SequenceEqual(["id", "data-x", "empty", "disabled"])).IsTrue();
        await Assert.That(paragraph.Attributes.Single(a => a.Name == "id").Value.Raw).IsEqualTo("first");
        await Assert.That(facts.Elements.Any(e => e.Attributes.Any(a => a.Name == "id" && a.Value.Decoded == "inert"))).IsFalse();

        var value = paragraph.Attributes.Single(a => a.Name == "data-x");
        await Assert.That(value.RawName.Raw).IsEqualTo("DATA-X");
        await Assert.That(value.RawName.Decoded).IsEqualTo("data-x");
        await Assert.That(value.Value.Raw).IsEqualTo("A\r\n&amp;&#x1F600;\uD800");
        await Assert.That(value.Value.Decoded).IsEqualTo("A\n&😀\uD800");
        var start = source.IndexOf("A\r\n&amp;", StringComparison.Ordinal);
        await Assert.That(value.Value.Source).IsEqualTo(new HtmlSpan(start, 18));
        HtmlTextSegment[] expected = [new(0, 1, new(start, 1)), new(1, 1, new(start + 1, 2)),
            new(2, 1, new(start + 3, 5)), new(3, 2, new(start + 8, 9)), new(5, 1, new(start + 17, 1))];
        await Assert.That(value.Value.Mapping.SequenceEqual(expected)).IsTrue();
        var empty = paragraph.Attributes.Single(a => a.Name == "empty").Value;
        var boolean = paragraph.Attributes.Single(a => a.Name == "disabled").Value;
        await Assert.That(empty.Source).IsEqualTo(new HtmlSpan(source.IndexOf("empty=''", StringComparison.Ordinal) + 7, 0));
        await Assert.That(boolean.Source).IsEqualTo(new HtmlSpan(source.IndexOf("disabled", StringComparison.Ordinal), 0));
        await Assert.That(empty.Raw == "" && empty.Decoded == "" && empty.Mapping.Count == 0).IsTrue();
        await Assert.That(boolean.Raw == "" && boolean.Decoded == "" && boolean.Mapping.Count == 0).IsTrue();

        var svg = facts.Elements.Single(e => e.Name == "svg");
        await Assert.That(svg.Namespace).IsEqualTo(HtmlNamespaces.Svg);
        await Assert.That(svg.Attributes.Single(a => a.Name == "viewBox").RawName.Raw).IsEqualTo("VIEWBOX");
        var href = svg.Attributes.Single(a => a.Namespace == HtmlNamespaces.XLink);
        await Assert.That(href.Name).IsEqualTo("href");
        await Assert.That(href.Prefix).IsEqualTo("xlink");
        await Assert.That(href.RawName.Raw).IsEqualTo("xlink:href");
        await Assert.That(href.Value.Decoded).IsEqualTo("&");
    }

    [Test]
    public async Task RawAttributesRejectMissingAndOutOfAttributeMappingsAndFreezeCallerCollections()
    {
        // HtmlTextBuilder permits source gaps while covering decoded text without gaps.
        const string source = "x='AX B'";
        var builder = new HtmlTextBuilder(3, 100, (_, _) => { });
        builder.Append("A", new(3, 1));
        builder.Append("B", new(6, 1));
        var sparse = builder.Build();
        var mapping = sparse.Segments.ToList();
        var name = new ParsedHtmlText("x", new(0, 1), [new(0, 1, new(0, 1))]);
        var success = HtmlRawValueSnapshot.TryCapture(source, sparse with { Segments = mapping }, default,
            out var valueSnapshot, out var valueReason);
        await Assert.That(success).IsTrue();
        await Assert.That(valueReason).IsNull();
        await Assert.That(HtmlRawValueSnapshot.TryCapture(source, name, default, out var nameSnapshot, out _)).IsTrue();
        var attributes = new List<HtmlRawAttributeSnapshot> { new("x", null, null, nameSnapshot!, valueSnapshot!) };
        var facts = new HtmlRawAttributeFacts([new HtmlRawElementSnapshot(0, "p", HtmlNamespaces.Html, null, attributes)],
            [new HtmlFactsProjectionCoverage(HtmlFactsProjection.RawAttributes, HtmlFactsCoverageStatus.Complete, [])]);
        var captured = facts.Elements.Single().Attributes.Single().Value;
        await Assert.That(facts.Coverage.Single(c => c.Projection == HtmlFactsProjection.RawAttributes).Status)
            .IsEqualTo(HtmlFactsCoverageStatus.Complete);
        await Assert.That(captured.Raw).IsEqualTo("AX B");
        await Assert.That(captured.Decoded).IsEqualTo("AB");
        mapping.Clear();
        attributes.Clear();
        await Assert.That(captured.Mapping.SequenceEqual(sparse.Segments)).IsTrue();
        await Assert.That(facts.Elements.Single().Attributes.Count).IsEqualTo(1);
        await Assert.That(() => ((IList<HtmlTextSegment>)captured.Mapping).Clear()).Throws<NotSupportedException>();
        await Assert.That(() => ((IList<HtmlRawAttributeSnapshot>)facts.Elements.Single().Attributes).Clear()).Throws<NotSupportedException>();
        await Assert.That(() => ((IList<HtmlRawElementSnapshot>)facts.Elements).Clear()).Throws<NotSupportedException>();

        // A shared source span also obeys Build's bounds; do not invent a disjoint-source rule.
        var sharedSource = new HtmlTextBuilder(3, 100, (_, _) => { });
        sharedSource.Append("A", new(3, 4));
        sharedSource.Append("B", new(3, 4));
        await Assert.That(HtmlRawValueSnapshot.TryCapture(source, sharedSource.Build(), default, out _, out _)).IsTrue();

        (ParsedHtmlText Value, string Reason)[] invalid =
        [
            (sparse with { Segments = [] }, "raw_attribute_mapping_missing"),
            (sparse with { Segments = [new(0, 1, new(0, 1)), new(1, 1, new(6, 1))] }, "raw_attribute_segment_outside_attribute_source"),
            (sparse with { Segments = [new(0, 1, new(3, 1))] }, "raw_attribute_decoded_mapping_incomplete"),
            (sparse with { Segments = [new(1, 1, new(3, 1)), new(2, 1, new(6, 1))] }, "raw_attribute_decoded_mapping_incomplete"),
            (new("", new(3, 1), []), "raw_attribute_empty_mapping_invalid")
        ];
        foreach (var item in invalid)
        {
            await Assert.That(HtmlRawValueSnapshot.TryCapture(source, item.Value, default, out var rejectedValue, out var reason)).IsFalse();
            await Assert.That(rejectedValue).IsNull();
            await Assert.That(reason!.Code).IsEqualTo(item.Reason);
            var incomplete = new HtmlRawAttributeFacts([], [new HtmlFactsProjectionCoverage(
                HtmlFactsProjection.RawAttributes, HtmlFactsCoverageStatus.Partial, [reason])]);
            await Assert.That(() => incomplete.RequireComplete()).Throws<HtmlRawFactsIncompleteException>();
        }
    }

    // Positive fixture owns its source and single parse; no second parse/decode in the producer.
    private static HtmlRawAttributeFacts CaptureParsed(string source) =>
        HtmlFacts.ParseWithRawAttributes(source).RawAttributes;
}
