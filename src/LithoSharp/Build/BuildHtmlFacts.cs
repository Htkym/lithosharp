using ParsedHtmlText = global::LithoSharp.HtmlParsing.HtmlText;
using System.Text;
using LithoSharp.HtmlParsing;
using LithoSharp.Quality;
using LithoSharp.Search;
using LithoSharp.Internal;

namespace LithoSharp.Build;

[Flags]
internal enum HtmlRawFactsDemand { None = 0, Search = 1, Quality = 2 }

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
    private readonly HtmlRawFactsDemand rawDemand;
    private long retainedBytes;
    private int parseCount;
    private int reuseCount;
    private bool qualityPhase;
    private bool closed;
    private bool disposed;

    internal BuildHtmlFacts(CancellationToken buildCancellation, bool qualityRequired = true,
        HtmlRawFactsDemand rawDemand = HtmlRawFactsDemand.None)
    {
        if ((rawDemand & ~(HtmlRawFactsDemand.Search | HtmlRawFactsDemand.Quality)) != 0)
            throw new ArgumentOutOfRangeException(nameof(rawDemand));
        if (!qualityRequired && (rawDemand & HtmlRawFactsDemand.Quality) != 0)
            throw new ArgumentException("Quality raw demand requires a Quality phase.", nameof(rawDemand));
        this.qualityRequired = qualityRequired;
        this.rawDemand = rawDemand;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(buildCancellation);
        lifetimeToken = lifetime.Token;
    }

    internal (int ParseCount, int ReuseCount, long RetainedBytes, int EntryCount) Counters
    { get { lock (gate) return (parseCount, reuseCount, retainedBytes, entries.Count); } }

    internal HtmlConsumerFacts Get(string source, CancellationToken consumerCancellation) =>
        GetAsync(source, consumerCancellation).GetAwaiter().GetResult().Facts;

    internal bool RequiresQualityRaw => (rawDemand & HtmlRawFactsDemand.Quality) != 0;

    internal async Task<HtmlRawAttributeFacts> GetRawAsync(string source, CancellationToken consumerCancellation,
        HtmlTreeOptions? options = null)
    {
        if ((rawDemand & HtmlRawFactsDemand.Search) == 0)
            throw new InvalidOperationException("Search raw demand was not declared before the first flight.");
        var result = await GetProjectionAsync(source, consumerCancellation, options, search: true).ConfigureAwait(false);
        return result.Facts.RawAttributes;
    }

    internal async Task<HtmlRawAttributeFacts> GetQualityRawAsync(string source, CancellationToken consumerCancellation,
        HtmlTreeOptions? options = null)
    {
        if (!RequiresQualityRaw)
            throw new InvalidOperationException("Quality raw demand was not declared before the first flight.");
        var result = await GetProjectionAsync(source, consumerCancellation, options, search: false).ConfigureAwait(false);
        return result.Facts.RawAttributes;
    }

    internal Task<(HtmlConsumerFacts Facts, bool NewParse)> GetAsync(string source,
        CancellationToken consumerCancellation, HtmlTreeOptions? options = null) =>
        GetProjectionAsync(source, consumerCancellation, options, search: true);

    internal async Task<(QualityHtmlFacts Facts, bool NewParse)> GetQualityAsync(string source,
        CancellationToken consumerCancellation)
    {
        var result = await GetProjectionAsync(source, consumerCancellation, null, search: false).ConfigureAwait(false);
        return (result.Facts.ToQuality(), result.NewParse);
    }

    internal async Task<(QualityHtmlFacts Facts, HtmlReferenceSnapshot Snapshot, bool NewParse)> GetQualityReferencesAsync(
        string source, HtmlPageIdentity identity, Uri pageUrl, CancellationToken consumerCancellation)
    {
        if (!RequiresQualityRaw) throw new InvalidOperationException("Reference snapshots require declared Quality raw demand.");
        return await GetQualitySnapshotAsync(source, identity, pageUrl, consumerCancellation).ConfigureAwait(false);
    }

    internal async Task<(QualityHtmlFacts Facts, HtmlReferenceSnapshot Snapshot, bool NewParse)> GetQualitySnapshotAsync(
        string source, HtmlPageIdentity identity, Uri pageUrl, CancellationToken consumerCancellation)
    {
        var result = await GetProjectionAsync(source, consumerCancellation, null, search: false).ConfigureAwait(false);
        var facts = result.Facts.ToQuality();
        var raw = RequiresQualityRaw ? result.Facts.RawAttributes : null;
        using var bindingCancellation = CancellationTokenSource.CreateLinkedTokenSource(consumerCancellation, lifetimeToken);
        var snapshot = BindQualityCore(facts, raw, identity, pageUrl, bindingCancellation.Token);
        return (facts, snapshot, result.NewParse);
    }

    internal static HtmlReferenceSnapshot BindDecodedQuality(QualityHtmlFacts facts, HtmlPageIdentity identity,
        Uri pageUrl, CancellationToken token) => BindQualityCore(facts, null, identity, pageUrl, token);

    // Only the owner supplies paired raw/Quality material. Durable decoded records have no raw entry point.
    private static HtmlReferenceSnapshot BindQualityCore(QualityHtmlFacts facts, HtmlRawAttributeFacts? raw,
        HtmlPageIdentity identity, Uri pageUrl, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(pageUrl);
        if (!pageUrl.IsAbsoluteUri || !string.Equals(pageUrl.AbsolutePath, identity.PublicPath, StringComparison.Ordinal))
            throw new ArgumentException("The page URL must identify the bound public route.", nameof(pageUrl));
        raw?.RequireComplete();
        var resolutionBase = pageUrl;
        if (facts.BaseHref is not null && Uri.TryCreate(pageUrl, facts.BaseHref, out var declaredBase)) resolutionBase = declaredBase;
        var references = new List<HtmlReference>();
        var cssReferences = new List<HtmlReference>();
        var cssReasons = new List<HtmlFactsCoverageReason>();
        var positions = new List<HtmlSnapshotPosition>();
        var anchors = new List<HtmlAnchorSnapshot>();
        var metadata = new List<HtmlMetadataSnapshot>();
        var canonicals = new List<HtmlCanonicalSnapshot>();
        if (raw is not null && raw.Elements.Count != facts.Elements.Length) throw new InvalidOperationException("Shared raw/Quality element order differs.");
        for (var i = 0; i < facts.Elements.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var element = facts.Elements[i];
            var material = raw?.Elements[i];
            if (material is not null && (material.Ordinal != i || material.Name != element.Name || material.Attributes.Count != element.Attributes.Length
                || !material.Attributes.Zip(element.Attributes).All(pair => pair.First.Name == pair.Second.Name
                    && pair.First.Namespace == pair.Second.Namespace && pair.First.Value.Decoded == pair.Second.Value)))
                throw new InvalidOperationException("Shared raw/Quality attributes differ.");
            positions.Add(new(element.Line, element.Column, material?.Source));
            Anchor("id");
            if (element.Name == "a") Anchor("name");
            if (element.Name == "meta") metadata.Add(new(i, Value("name"), Value("property"), Value("content"),
                Attribute("content")?.Value, Attribute("name")?.Value, Attribute("property")?.Value));
            var canonical = SiteQualityValidator.HasRel(element, "canonical");
            if (element.Name == "link" && canonical)
            {
                var href = Value("href");
                Uri? address = null;
                if (href is not null && Uri.TryCreate(resolutionBase, href, out var resolved)) address = resolved;
                canonicals.Add(new(i, href, address, Attribute("href")?.Value));
            }
            var style = Value("style");
            if (element.Name == "style" || style is not null)
            {
                var css = element.Name == "style" ? element.StyleText! : style!;
                var collected = CssUrlScanner.CollectCandidates(css, token, includeImports: element.Name == "style");
                cssReasons.AddRange(collected.Coverage.Reasons);
                HtmlSourceMap? input = element.Name == "style"
                    ? raw?.Materials?.Styles.GetValueOrDefault(i)
                    : Attribute("style") is { } styleAttribute ? HtmlSourceMap.FromRaw(styleAttribute.Value, HtmlFactsProjection.InlineCss) : null;
                if (input is not null && input.Value != css) throw new InvalidOperationException("Paired CSS input differs.");
                var ordinal = 0;
                foreach (var candidate in collected.Candidates)
                {
                    token.ThrowIfCancellationRequested();
                    var issue = SiteQualityUrlRules.Resolve(candidate.Value, true, resolutionBase, out var address);
                    cssReferences.Add(new(i, element.Name == "style" ? "#style" : "style", ordinal++,
                        element.Name == "style" ? null : Attribute("style")?.Value, candidate.Value, address, issue,
                        HtmlReferenceRole.Css, false, true, element.Name == "style" ? candidate.Value : style,
                        input?.Compose(candidate.Value, candidate.Source, candidate.Mapping, HtmlFactsProjection.InlineCss)));
                }
            }
            if (element.Name == "base") { Add("href", HtmlReferenceRole.Base, false, false); continue; }
            if (canonical) { Add("href", HtmlReferenceRole.Canonical, false, false); continue; }
            if (!element.Attributes.Any(attribute => attribute.Name is "href" or "src" or "srcset" or "poster")
                && !(element.Name == "object" && QualityHtmlFacts.Attribute(element, "data") is not null)
                && !(element.Name == "form" && QualityHtmlFacts.Attribute(element, "action") is not null)
                && !(element.Name == "meta" && QualityHtmlFacts.Attribute(element, "property") is not null)) continue;
            Add("href", element.Name is "a" or "area" ? HtmlReferenceRole.Navigation : HtmlReferenceRole.Resource,
                element.Name is "a" or "area", element.Name is not ("a" or "area"));
            Add("src", HtmlReferenceRole.Resource, false, true);
            Add("poster", HtmlReferenceRole.Resource, false, true);
            if (element.Name == "object") Add("data", HtmlReferenceRole.Resource, false, true);
            if (element.Name == "form") Add("action", HtmlReferenceRole.FormAction, true, false);
            var srcset = Value("srcset");
            if (srcset is not null)
            {
                var candidate = 0;
                foreach (var value in SrcsetUrlScanner.ReadCandidates(srcset, token))
                    Capture("srcset", Attribute("srcset")?.Value, srcset, value.Value, HtmlReferenceRole.SrcsetCandidate,
                        false, true, candidate++, value.Source);
            }
            if (string.Equals(QualityHtmlFacts.Attribute(element, "property"), "og:image", StringComparison.OrdinalIgnoreCase))
            {
                var content = Attribute("content");
                var decoded = Value("content");
                Capture("content", content?.Value, decoded, decoded ?? string.Empty, HtmlReferenceRole.MetaImage, false, true, 0);
            }

            string? Value(string name) => QualityHtmlFacts.Attribute(element, name);
            HtmlRawAttributeSnapshot? Attribute(string name) => material?.Attributes.FirstOrDefault(a => a.Namespace is null && a.Name == name);
            void Anchor(string name)
            {
                if (Value(name) is { Length: > 0 } value) anchors.Add(new(i, name, value, Attribute(name)?.Value));
            }
            void Add(string name, HtmlReferenceRole role, bool navigation, bool resource)
            {
                if (Value(name) is { } value) Capture(name, Attribute(name)?.Value, value, value, role, navigation, resource, 0);
            }
            void Capture(string name, HtmlRawValueSnapshot? value, string? decoded, string unresolved, HtmlReferenceRole role,
                bool navigation, bool resource, int candidate, HtmlSpan? candidateRange = null)
            {
                token.ThrowIfCancellationRequested();
                var issue = SiteQualityUrlRules.Resolve(unresolved, resource,
                    role == HtmlReferenceRole.Base ? pageUrl : resolutionBase, out var resolved);
                var origin = value is null ? null : HtmlSourceMap.FromRaw(value, HtmlFactsProjection.References);
                references.Add(new(i, name, candidate, value, unresolved, resolved, issue, role, navigation, resource, decoded,
                    candidateRange is { } range ? origin?.Slice(range) : origin));
            }
        }
        token.ThrowIfCancellationRequested();
        var coverage = Enum.GetValues<HtmlFactsProjection>().Select(projection => projection switch
        {
            HtmlFactsProjection.RawAttributes => new HtmlFactsProjectionCoverage(projection,
                raw is null ? HtmlFactsCoverageStatus.NotRequested : HtmlFactsCoverageStatus.Complete, []),
            HtmlFactsProjection.InlineCss => new HtmlFactsProjectionCoverage(projection,
                cssReasons.Count == 0 ? HtmlFactsCoverageStatus.Complete : HtmlFactsCoverageStatus.Partial, cssReasons),
            HtmlFactsProjection.Search => new HtmlFactsProjectionCoverage(projection, HtmlFactsCoverageStatus.NotRequested, []),
            HtmlFactsProjection.Headings => new HtmlFactsProjectionCoverage(projection,
                raw?.Materials is null ? HtmlFactsCoverageStatus.NotRequested : HtmlFactsCoverageStatus.Complete, []),
            _ => new HtmlFactsProjectionCoverage(projection, HtmlFactsCoverageStatus.Complete, []),
        });
        var sourceCoverage = Enum.GetValues<HtmlFactsProjection>().Select(projection =>
        {
            if (raw is null) return new HtmlFactsProjectionCoverage(projection, HtmlFactsCoverageStatus.NotRequested, []);
            IEnumerable<HtmlFactsCoverageReason> reasons = projection switch
            {
                HtmlFactsProjection.References => references.Where(reference => reference.Value is not null)
                    .SelectMany(reference => reference.Candidate?.Coverage.Reasons ?? [new("reference_candidate_source_unavailable", null)]),
                HtmlFactsProjection.InlineCss => cssReferences.SelectMany(reference =>
                    reference.Candidate?.Coverage.Reasons ?? [new("css_candidate_source_unavailable", null)]),
                HtmlFactsProjection.Metadata => raw.Materials?.Title.Coverage.Reasons ?? [],
                HtmlFactsProjection.Headings => raw.Materials?.Headings.SelectMany(h => h.Text.Coverage.Reasons.Concat(h.SearchTitle.Coverage.Reasons)) ?? [],
                _ => [],
            };
            if (projection == HtmlFactsProjection.Search) return new HtmlFactsProjectionCoverage(projection, HtmlFactsCoverageStatus.NotRequested, []);
            var errors = reasons.ToArray();
            return new HtmlFactsProjectionCoverage(projection, errors.Length == 0 ? HtmlFactsCoverageStatus.Complete : HtmlFactsCoverageStatus.Partial, errors);
        });
        return new(identity, pageUrl, resolutionBase, facts.Title, positions, anchors, metadata, canonicals,
            references, cssReferences, coverage, raw?.Materials?.Title, raw?.Materials?.Headings, sourceCoverage);
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
        HtmlFacts parsed;
        HtmlRawAttributeFacts? raw = null;
        if (RequiresQualityRaw || (captureSearch && (rawDemand & HtmlRawFactsDemand.Search) != 0))
        {
            var captured = HtmlFacts.ParseWithRawAttributes(key.Source, lifetimeToken, options,
                includeSearchMaterials: captureSearch && (rawDemand & HtmlRawFactsDemand.Search) != 0);
            parsed = captured.Facts;
            raw = captured.RawAttributes;
            raw.RequireComplete();
        }
        else parsed = HtmlFacts.Parse(key.Source, lifetimeToken, options);
        var projection = HtmlConsumerFacts.Capture(parsed, captureSearch, qualityRequired, lifetimeToken, raw);
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
                var compact = flight.Result.QualityOnly(keepRaw: RequiresQualityRaw);
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
    private readonly HtmlRawAttributeFacts? raw;
    private readonly CancellationToken consumerCancellation;
    private readonly CancellationToken scopeCancellation;

    private HtmlConsumerFacts(SearchProjection? search, QualityProjection? quality,
        CancellationToken consumer = default, CancellationToken scope = default, HtmlRawAttributeFacts? raw = null)
    { this.search = search; this.quality = quality; this.raw = raw; consumerCancellation = consumer; scopeCancellation = scope; }

    internal static HtmlConsumerFacts Parse(string source, CancellationToken token) =>
        Capture(HtmlFacts.Parse(source, token), true, false, token).ForConsumer(token);

    internal static HtmlConsumerFacts Capture(HtmlFacts parsed, bool includeSearch, bool includeQuality, CancellationToken token,
        HtmlRawAttributeFacts? raw = null)
    {
        var source = raw?.Materials?.Search;
        IReadOnlyList<SearchSection>? sections = source is null ? null : Array.AsReadOnly(source.Sections.Select(section =>
            new SearchSection(section.Heading.SearchTitle.Value, section.Heading.Anchor!, section.Body.Value)).ToArray());
        var search = includeSearch ? new SearchProjection(source?.Body.Value ?? parsed.BodyText,
            source?.IndexText.Value ?? parsed.SearchText, sections ?? parsed.SearchSections(), source) : null;
        var quality = includeQuality ? QualityProjection.Freeze(QualityHtmlFacts.From(parsed, token)) : null;
        token.ThrowIfCancellationRequested();
        return new(search, quality, raw: raw);
    }

    internal HtmlConsumerFacts ForConsumer(CancellationToken consumer, CancellationToken scope = default) => new(search, quality, consumer, scope, raw);
    internal HtmlConsumerFacts QualityOnly(bool keepRaw = false) => new(null, quality, raw: keepRaw ? raw?.WithoutSearchMaterials() : null);
    internal HtmlRawAttributeFacts RawAttributes
    { get { Check(); return raw ?? throw new InvalidOperationException("Raw attributes were not requested for this phase."); } }
    internal string BodyText { get { Check(); return RequiredSearch.Body; } }
    internal string SearchText { get { Check(); return RequiredSearch.Text; } }
    internal IReadOnlyList<SearchSection> SearchSections() { Check(); return RequiredSearch.Sections; }
    internal HtmlSearchSourceSnapshot? SearchSource { get { Check(); return RequiredSearch.Source; } }
    internal HtmlFactsProjectionCoverage SearchSourceCoverage
    {
        get
        {
            Check();
            var source = RequiredSearch.Source;
            if (source is null) return new(HtmlFactsProjection.Search, HtmlFactsCoverageStatus.NotRequested, []);
            return source.Coverage;
        }
    }

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
    internal long RetentionCharge => 256L + (search?.Charge ?? 0) + (quality?.Charge ?? 0) + (raw?.RetentionCharge ?? 0);

    private sealed record SearchProjection(string Body, string Text, IReadOnlyList<SearchSection> Sections, HtmlSearchSourceSnapshot? Source = null)
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
