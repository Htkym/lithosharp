using ParsedHtmlText = global::LithoSharp.HtmlParsing.HtmlText;
using System.Text;
using LithoSharp.HtmlParsing;
using LithoSharp.Quality;
using LithoSharp.Search;

namespace LithoSharp.Build;

// Known build consumers share immutable projections, never a retained tree or site binding.
// Source identity/projections scale with the input; no new site byte/key quota is imposed.
internal sealed class BuildHtmlFacts : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<Key, Task<HtmlConsumerFacts>> entries = new();
    private readonly SemaphoreSlim parser = new(1, 1);
    private readonly CancellationTokenSource lifetime;
    private readonly CancellationToken lifetimeToken;
    private readonly bool qualityRequired;
    private long retainedBytes;
    private int parseCount;
    private int reuseCount;
    private bool qualityPhase;
    private bool closed;
    private bool disposed;

    internal BuildHtmlFacts(CancellationToken buildCancellation, bool qualityRequired = true)
    {
        this.qualityRequired = qualityRequired;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(buildCancellation);
        lifetimeToken = lifetime.Token;
    }

    internal (int ParseCount, int ReuseCount, long RetainedBytes, int EntryCount) Counters
    { get { lock (gate) return (parseCount, reuseCount, retainedBytes, entries.Count); } }

    internal HtmlConsumerFacts Get(string source, CancellationToken consumerCancellation) =>
        GetAsync(source, consumerCancellation).GetAwaiter().GetResult().Facts;

    internal Task<(HtmlConsumerFacts Facts, bool NewParse)> GetAsync(string source,
        CancellationToken consumerCancellation, HtmlTreeOptions? options = null) =>
        GetProjectionAsync(source, consumerCancellation, options, search: true);

    internal async Task<(QualityHtmlFacts Facts, bool NewParse)> GetQualityAsync(string source,
        CancellationToken consumerCancellation)
    {
        var result = await GetProjectionAsync(source, consumerCancellation, null, search: false).ConfigureAwait(false);
        return (result.Facts.ToQuality(), result.NewParse);
    }

    private async Task<(HtmlConsumerFacts Facts, bool NewParse)> GetProjectionAsync(string source,
        CancellationToken consumerCancellation, HtmlTreeOptions? options, bool search)
    {
        ArgumentNullException.ThrowIfNull(source);
        consumerCancellation.ThrowIfCancellationRequested();
        Task<HtmlConsumerFacts> flight;
        bool added;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            lifetimeToken.ThrowIfCancellationRequested();
            if (closed || (search && qualityPhase))
                throw new InvalidOperationException("The requested HTML consumer phase has finished; reparsing is not permitted.");
            if (!search && !qualityRequired) throw new InvalidOperationException("This build has no Quality consumer.");
            var snapshot = SnapshotOptions(options ?? new());
            var key = new Key(source, Profile(snapshot)); // Raw ordinal UTF-16 equality, not hash-only identity.
            consumerCancellation.ThrowIfCancellationRequested();
            lifetimeToken.ThrowIfCancellationRequested();
            added = !entries.TryGetValue(key, out flight!);
            if (added)
            {
                retainedBytes += 1024L + 2L * source.Length + 2L * key.Profile.Length;
                var captureSearch = !qualityPhase;
                flight = Task.Run(() => ParseAsync(key, snapshot, captureSearch));
                entries.Add(key, flight);
            }
            else reuseCount++;
        }
        var facts = await flight.WaitAsync(consumerCancellation).ConfigureAwait(false);
        consumerCancellation.ThrowIfCancellationRequested();
        lifetimeToken.ThrowIfCancellationRequested();
        return (facts.ForConsumer(consumerCancellation, lifetimeToken), added);
    }

    private async Task<HtmlConsumerFacts> ParseAsync(Key key, HtmlTreeOptions options, bool captureSearch)
    {
        await parser.WaitAsync(lifetimeToken).ConfigureAwait(false);
        try { return ParseAndProject(key, options, captureSearch); }
        finally { parser.Release(); }
    }

    private HtmlConsumerFacts ParseAndProject(Key key, HtmlTreeOptions options, bool captureSearch)
    {
        lifetimeToken.ThrowIfCancellationRequested();
        lock (gate) parseCount++;
        var parsed = HtmlFacts.Parse(key.Source, lifetimeToken, options);
        var projection = HtmlConsumerFacts.Capture(parsed, captureSearch, qualityRequired, lifetimeToken);
        lock (gate) retainedBytes += projection.RetentionCharge;
        return projection; // No tree, line-start array or cancellation token enters the retained value.
    }

    // Prepare/search/render have finished. Keep only the Quality observations for possible equal final HTML.
    internal async Task BeginQualityAsync()
    {
        Task<HtmlConsumerFacts>[] flights;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (closed || !qualityRequired) throw new InvalidOperationException("There is no pending Quality phase.");
            qualityPhase = true;
            flights = entries.Values.ToArray();
        }
        await ObserveAsync(flights).ConfigureAwait(false);
        lifetimeToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            foreach (var key in entries.Keys.ToArray())
            {
                var flight = entries[key];
                if (!flight.IsCompletedSuccessfully) continue; // Faults remain terminal faults, not empty facts.
                var compact = flight.Result.QualityOnly();
                retainedBytes -= flight.Result.RetentionCharge - compact.RetentionCharge;
                entries[key] = Task.FromResult(compact);
            }
        }
    }

    // All known consumers have obtained their own projections. Late requests fail, never silently reparse.
    internal async Task ReleaseAsync()
    {
        Task<HtmlConsumerFacts>[] flights;
        lock (gate) { closed = true; flights = entries.Values.ToArray(); }
        await ObserveAsync(flights).ConfigureAwait(false);
        lock (gate) { entries.Clear(); retainedBytes = 0; }
    }

    public async ValueTask DisposeAsync()
    {
        lock (gate) { if (disposed) return; disposed = true; }
        lifetime.Cancel();
        await ReleaseAsync().ConfigureAwait(false);
        parser.Dispose();
        lifetime.Dispose();
    }

    private static async Task ObserveAsync(Task<HtmlConsumerFacts>[] flights)
    {
        try { await Task.WhenAll(flights).ConfigureAwait(false); }
        catch { /* Observe failed/canceled flights. Their consumers still receive the original failure. */ }
    }

    private static HtmlTreeOptions SnapshotOptions(HtmlTreeOptions options)
    {
        if (options.Fragment is not { } fragment) return options;
        return options with { Fragment = fragment with
        {
            Attributes = Array.AsReadOnly((fragment.Attributes ?? []).Select(attribute => new HtmlAttribute(
                Freeze(attribute.Name), Freeze(attribute.Value))).ToArray())
        } };
        static ParsedHtmlText Freeze(ParsedHtmlText text) => text with { Segments = Array.AsReadOnly(text.Segments.ToArray()) };
    }

    private string Profile(HtmlTreeOptions options)
    {
        var identity = new StringBuilder("tree/1;tokenizer/1;default-limits/1;").Append(options.Scripting ? '1' : '0');
        if (options.Fragment is not { } context) return identity.Append(";document").ToString();
        identity.Append(";fragment;"); Text(context.Name); Text(context.Namespace);
        Number((int)context.OwnerDocumentMode); Number(context.HasExternalFormAncestor ? 1 : 0);
        Number(context.Attributes!.Count);
        foreach (var attribute in context.Attributes) { Mapped(attribute.Name); Mapped(attribute.Value); }
        return identity.ToString();
        void Text(string value) { Number(value.Length); identity.Append(value); }
        void Number(int value) => identity.Append(value.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':');
        void Mapped(ParsedHtmlText text)
        {
            Text(text.Value); Number(text.Source.Start); Number(text.Source.Length); Number(text.Segments.Count);
            foreach (var segment in text.Segments)
            {
                lifetimeToken.ThrowIfCancellationRequested();
                Number(segment.ValueStart); Number(segment.ValueLength); Number(segment.Source.Start); Number(segment.Source.Length);
            }
        }
    }

    private sealed record Key(string Source, string Profile);
}

// The three existing consumers need these summaries, not arbitrary tree access.
// Shared collections are read-only; the old mutable Quality DTO is freshly copied for each consumer.
internal sealed class HtmlConsumerFacts
{
    private readonly SearchProjection? search;
    private readonly QualityProjection? quality;
    private readonly CancellationToken consumerCancellation;
    private readonly CancellationToken scopeCancellation;

    private HtmlConsumerFacts(SearchProjection? search, QualityProjection? quality,
        CancellationToken consumer = default, CancellationToken scope = default)
    { this.search = search; this.quality = quality; consumerCancellation = consumer; scopeCancellation = scope; }

    internal static HtmlConsumerFacts Parse(string source, CancellationToken token) =>
        Capture(HtmlFacts.Parse(source, token), true, false, token).ForConsumer(token);

    internal static HtmlConsumerFacts Capture(HtmlFacts parsed, bool includeSearch, bool includeQuality, CancellationToken token)
    {
        var search = includeSearch ? new SearchProjection(parsed.BodyText, parsed.SearchText, parsed.SearchSections()) : null;
        var quality = includeQuality ? QualityProjection.Freeze(QualityHtmlFacts.From(parsed, token)) : null;
        token.ThrowIfCancellationRequested();
        return new(search, quality);
    }

    internal HtmlConsumerFacts ForConsumer(CancellationToken consumer, CancellationToken scope = default) => new(search, quality, consumer, scope);
    internal HtmlConsumerFacts QualityOnly() => new(null, quality);
    internal string BodyText { get { Check(); return RequiredSearch.Body; } }
    internal string SearchText { get { Check(); return RequiredSearch.Text; } }
    internal IReadOnlyList<SearchSection> SearchSections() { Check(); return RequiredSearch.Sections; }

    private SearchProjection RequiredSearch => search ?? throw new InvalidOperationException("Search projections were not requested in this phase.");
    internal QualityHtmlFacts ToQuality()
    {
        Check();
        var source = quality ?? throw new InvalidOperationException("Quality was not part of this consumer plan.");
        var elements = new QualityHtmlElement[source.Elements.Count];
        for (var i = 0; i < elements.Length; i++)
        {
            Check();
            var element = source.Elements[i];
            elements[i] = new(element.Name, element.Attributes.ToArray(), element.Line, element.Column, element.StyleText);
        }
        Check();
        return new(source.Title, source.BaseHref, source.Anchors.ToArray(), elements);
    }

    private void Check() { consumerCancellation.ThrowIfCancellationRequested(); scopeCancellation.ThrowIfCancellationRequested(); }
    // Diagnostic accounting only; this is not an admission quota or a process heap measurement.
    internal long RetentionCharge => 256L + (search?.Charge ?? 0) + (quality?.Charge ?? 0);

    private sealed record SearchProjection(string Body, string Text, IReadOnlyList<SearchSection> Sections)
    {
        internal long Charge => 128L + 2L * (Body.Length + (long)Text.Length)
            + Sections.Sum(section => 64L + 2L * (section.Title.Length + (long)section.Anchor.Length + section.Body.Length));
    }
    private sealed record Element(string Name, IReadOnlyList<QualityHtmlAttribute> Attributes, int? Line, int? Column, string? StyleText);
    private sealed record QualityProjection(string Title, string? BaseHref, IReadOnlyList<string> Anchors, IReadOnlyList<Element> Elements)
    {
        internal static QualityProjection Freeze(QualityHtmlFacts facts) => new(facts.Title, facts.BaseHref,
            Array.AsReadOnly(facts.Anchors), Array.AsReadOnly(facts.Elements.Select(element => new Element(element.Name,
                Array.AsReadOnly(element.Attributes), element.Line, element.Column, element.StyleText)).ToArray()));
        internal long Charge => 128L + 2L * (Title.Length + (long)(BaseHref?.Length ?? 0))
            + Anchors.Sum(anchor => 32L + 2L * anchor.Length)
            + Elements.Sum(element => 128L + 2L * (element.Name.Length + (long)(element.StyleText?.Length ?? 0))
                + element.Attributes.Sum(attribute => 64L + 2L * (attribute.Name.Length + (long)(attribute.Namespace?.Length ?? 0) + attribute.Value.Length)));
    }
}
