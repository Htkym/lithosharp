using System.Globalization;
using Syntamark.Compilation;

namespace LithoSharp.Content.Compilation;

internal sealed class MarkdownFootnoteRenderPlan
{
    private readonly Dictionary<int, string> definitionIds = new();
    private readonly Dictionary<(int Number, int Occurrence), string> referenceIds = new();
    private readonly Dictionary<int, IReadOnlyList<LithoFootnoteReference>> referencesByNumber;

    private MarkdownFootnoteRenderPlan(IReadOnlyList<LithoFootnoteDefinition> definitions,
        IReadOnlyList<LithoFootnoteReference> references, IReadOnlyList<string> headingIds)
    {
        Definitions = definitions;
        References = references;
        referencesByNumber = references.GroupBy(static reference => reference.Number!.Value)
            .ToDictionary(static group => group.Key, static group => (IReadOnlyList<LithoFootnoteReference>)group.ToArray());
        var used = headingIds.ToHashSet(StringComparer.Ordinal);
        foreach (var definition in definitions)
            definitionIds.Add(definition.Number!.Value, Allocate("fn-" + Number(definition.Number.Value)));
        foreach (var reference in references)
        {
            var key = (Number: reference.Number!.Value, reference.Occurrence);
            referenceIds.Add(key, Allocate("fnref-" + Number(key.Number)
                + (key.Occurrence == 1 ? string.Empty : "-" + Number(key.Occurrence))));
        }

        string Allocate(string candidate)
        {
            var value = candidate;
            for (var suffix = 2; !used.Add(value); suffix++)
                value = candidate + "-" + Number(suffix);
            return value;
        }
    }

    internal IReadOnlyList<LithoFootnoteDefinition> Definitions { get; }
    internal IReadOnlyList<LithoFootnoteReference> References { get; }
    internal string? DefinitionId(int number) => definitionIds.GetValueOrDefault(number);
    internal string? ReferenceId(int number, int occurrence) => referenceIds.GetValueOrDefault((number, occurrence));
    internal IReadOnlyList<LithoFootnoteReference> ReferencesFor(int number) => referencesByNumber.GetValueOrDefault(number) ?? [];
    internal static string Number(int number) => number.ToString(CultureInfo.InvariantCulture);

    internal static MarkdownFootnoteRenderPlan Create(IReadOnlyList<LithoBlock> blocks,
        IReadOnlyList<string> headingIds, CancellationToken cancellationToken = default)
    {
        var definitions = Walk(blocks, descendDefinitions: true)
            .OfType<LithoFootnoteDefinition>().Where(static item => !item.IsDuplicate && item.Number.HasValue)
            .OrderBy(static item => item.Number).ToArray();
        var references = new List<LithoFootnoteReference>();
        Collect(blocks);
        foreach (var definition in definitions)
            Collect(definition.Children);
        references.Sort(static (left, right) => left.Span.Start.CompareTo(right.Span.Start));
        return new MarkdownFootnoteRenderPlan(definitions, references, headingIds);

        void Collect(IReadOnlyList<LithoBlock> group)
        {
            foreach (var block in Walk(group, descendDefinitions: false))
            {
                IEnumerable<IReadOnlyList<LithoInline>> inlines = block switch
                {
                    LithoParagraph paragraph => [paragraph.Inlines],
                    LithoHeading heading => [heading.Inlines],
                    LithoTable table => table.Rows.SelectMany(static row => row),
                    _ => [],
                };
                foreach (var inlineGroup in inlines)
                {
                    ArgumentNullException.ThrowIfNull(inlineGroup, "inlines");
                    foreach (var (inline, exit) in LithoInline.Walk(inlineGroup, includeImageLabels: false, cancellationToken: cancellationToken))
                        if (!exit && inline is LithoFootnoteReference { Number: not null } reference)
                            references.Add(reference);
                }
            }
        }

        IEnumerable<LithoBlock> Walk(IReadOnlyList<LithoBlock> group, bool descendDefinitions)
        {
            var pending = new Stack<LithoBlock>(group.Reverse());
            while (pending.TryPop(out var block))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return block;
                var children = block switch
                {
                    LithoFootnoteDefinition definition when descendDefinitions => definition.Children,
                    LithoQuote quote => quote.Children,
                    LithoAdmonition admonition => admonition.Children,
                    LithoDirective directive => directive.Children,
                    LithoList list => list.Items.SelectMany(static item => item.Children).ToArray(),
                    _ => null,
                };
                if (children is not null)
                    for (var index = children.Count - 1; index >= 0; index--)
                        pending.Push(children[index]);
            }
        }
    }
}
