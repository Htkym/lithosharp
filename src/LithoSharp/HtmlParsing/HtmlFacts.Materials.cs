using System.Collections.ObjectModel;
using ParsedHtmlText = global::LithoSharp.HtmlParsing.HtmlText;

namespace LithoSharp.HtmlParsing;

internal sealed partial class HtmlFacts
{
    // Called only inside the source-only factory immediately after that source's Parse.
    private HtmlTextMaterials CaptureTextMaterialsCore(string source, bool includeSearch, CancellationToken token)
    {
        var chunks = new Dictionary<ParsedHtmlText, HtmlSourceMap>(ReferenceEqualityComparer.Instance);
        var ordinals = Elements.Select((element, ordinal) => (element.Id, ordinal)).ToDictionary(pair => pair.Id, pair => pair.ordinal);
        HtmlSourceMap Mapped(int root, HtmlFactsProjection projection, bool search = false, bool separate = false)
        {
            var builder = new HtmlSourceMapBuilder(projection);
            foreach (var chunk in TextContentChunks(root, search, separate))
            {
                token.ThrowIfCancellationRequested();
                if (chunk is null)
                {
                    builder.Append(" ", null, "html_structure_separator");
                    continue;
                }
                if (!chunks.TryGetValue(chunk, out var map))
                {
                    map = HtmlRawValueSnapshot.TryCapture(source, chunk, token, out var raw, out var reason)
                        ? HtmlSourceMap.FromRaw(raw!, projection)
                        : HtmlSourceMap.Unknown(chunk.Value, projection, reason!.Code);
                    chunks.Add(chunk, map);
                }
                builder.Append(map);
            }
            return builder.Build();
        }
        var titleNode = Elements.FirstOrDefault(element => element.Name == "title" && element.Namespace == HtmlNamespaces.Html);
        var title = titleNode is null ? new HtmlSourceMapBuilder(HtmlFactsProjection.Metadata).Build()
            : Mapped(titleNode.Id, HtmlFactsProjection.Metadata).Normalize(asciiWhitespace: true);
        var headings = Elements.Where(element => element.Name is "h1" or "h2" or "h3" or "h4" or "h5" or "h6")
            .Select(element => new HtmlHeadingMaterial(ordinals[element.Id], element.Name![1] - '0', Attribute(element, "id"),
                element.Source, Mapped(element.Id, HtmlFactsProjection.Headings),
                Mapped(element.Id, HtmlFactsProjection.Headings, search: true))).ToArray();
        var byId = headings.ToDictionary(heading => Elements[heading.ElementOrdinal].Id);
        var styles = Elements.Where(element => element.Name == "style").ToDictionary(element => ordinals[element.Id],
            element => Mapped(element.Id, HtmlFactsProjection.InlineCss));
        HtmlSearchSourceSnapshot? searchMaterial = null;
        if (includeSearch)
        {
            var body = Body is { } bodyId ? Mapped(bodyId, HtmlFactsProjection.Search, search: true)
                : new HtmlSourceMapBuilder(HtmlFactsProjection.Search).Build();
            var index = Body is { } indexId ? Mapped(indexId, HtmlFactsProjection.Search, search: true, separate: true)
                : new HtmlSourceMapBuilder(HtmlFactsProjection.Search).Build();
            var sections = SectionRoots().Select(section =>
            {
                var builder = new HtmlSourceMapBuilder(HtmlFactsProjection.Search);
                foreach (var root in section.Body)
                {
                    builder.Append(Mapped(root, HtmlFactsProjection.Search, search: true));
                    builder.Append(" ", null, "section_sibling_separator");
                }
                var mapped = builder.Build();
                return new HtmlSearchSectionMaterial(byId[section.Heading.Id],
                    mapped.Normalize(false, SiteGenerator.NormalizeForIndex(mapped.Value).Length));
            }).ToArray();
            searchMaterial = new(body, index, index.Normalize(false, SiteGenerator.NormalizeForIndex(index.Value).Length),
                Array.AsReadOnly(sections));
        }
        token.ThrowIfCancellationRequested();
        return new(title, Array.AsReadOnly(headings), new ReadOnlyDictionary<int, HtmlSourceMap>(styles), searchMaterial);
    }
}
