using Syntamark.Compilation;
using LithoSharp.Content.Compilation;

namespace LithoSharp.Inspection;

// Contract logical accounting, not actual heap size. Count each DTO field occurrence;
// source segments cost32 and are not also charged as64-byte facts.
internal static class MarkdownLogicalSize
{
    internal static long Measure(MdDocumentFacts document, CancellationToken cancellationToken)
    {
        long bytes = 0;
        void Fact() { cancellationToken.ThrowIfCancellationRequested(); bytes = checked(bytes + 64); }
        void Text(string? value) { if (value is not null) bytes = checked(bytes + 2L * value.Length); }
        void Projection(MdTextProjection value)
        {
            Fact(); Text(value.Text);
            foreach (var segment in value.SourceSegments)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bytes = checked(bytes + 32);
            }
        }
        void Region(MdTextRegion value) { Fact(); Text(value.Kind); Projection(value.Content); }
        Fact(); Text(document.RawText); Text(document.ScopeId); Text(document.SourceId); Text(document.SourceVersion);
        Text(document.TextHash); Text(document.ParserVersion); Text(document.OptionsHash);
        Text("1.0"); Text("lithosharp-markdown/1"); // envelope contract/profile held by the bound cache key
        Fact(); // frontmatter
        if (document.FrontMatter.Root is { } root)
        {
            var pending = new Stack<MdYamlNode>(); pending.Push(root);
            while (pending.TryPop(out var node))
            {
                Fact(); Projection(node.Scalar);
                foreach (var child in node.Children) pending.Push(child);
            }
        }
        foreach (var heading in document.Headings) { Fact(); Text(heading.LocalKey); Text(heading.Anchor); Projection(heading.Text); }
        foreach (var section in document.Sections) { Fact(); Text(section.LocalKey); Text(section.ParentLocalKey); Text(section.HeadingLocalKey); }
        foreach (var link in document.Links) { Fact(); Text(link.Target); Text(link.Title); Text(link.ReferenceLabel); Projection(link.Label); }
        foreach (var fence in document.Fences) { Fact(); Text(fence.Info); Projection(fence.Content); }
        foreach (var region in document.TextRegions) Region(region);
        foreach (var diagnostic in document.Diagnostics) { Fact(); Text(diagnostic.Id); Text(diagnostic.Message); Text(diagnostic.Origin); Text(diagnostic.Reason); }
        foreach (var capability in document.Capabilities) { Fact(); Text(capability.Feature); }
        Fact(); // coverage
        foreach (var reason in document.Coverage.Reasons) Text(reason);
        foreach (var region in document.Coverage.UnprojectedRegions) Region(region);
        cancellationToken.ThrowIfCancellationRequested();
        return bytes;
    }
}
