using System.Text;
using System.Text.Json;
using LithoSharp.AgentReady;
using LithoSharp.Build;
using LithoSharp.Compatibility;
using LithoSharp.Content;
using LithoSharp.Content.Compilation;
using LithoSharp.Diagnostics;
using LithoSharp.Pages;
using LithoSharp.Quality;
using LithoSharp.Routing;
using Syntamark.Compilation;

namespace LithoSharp;

public sealed partial class SiteGenerator
{
    // One state reference is shared with typed provider contexts before HTML rendering.
    // Planning completes before any render action consumes these immutable snapshots.
    internal sealed class AgentReadyBuildState(AgentReadyOptions options, bool generateLlms)
    {
        internal AgentReadyOptions Options { get; } = options;
        internal bool GenerateLlms { get; } = generateLlms;
        internal bool Preparing { get; set; }
        internal string GlobalIdentity { get; set; } = string.Empty;
        internal IReadOnlyList<AgentReadyPlannedPage> Pages { get; set; } = [];
        internal IReadOnlyDictionary<string, AgentReadyPlannedPage> ByPath { get; set; } = new Dictionary<string, AgentReadyPlannedPage>();
        internal IReadOnlyDictionary<string, AgentReadyPlannedPage> ByCanonical { get; set; } = new Dictionary<string, AgentReadyPlannedPage>();
        internal IReadOnlyDictionary<string, string> TextArtifacts { get; set; } = new Dictionary<string, string>();
        internal IReadOnlyList<SiteDiagnostic> Diagnostics { get; set; } = [];
    }

    internal sealed record AgentReadyPlannedPage(SiteRoute Html, SiteRoute? Markdown, PageMetadata Metadata,
        AgentReadyPolicyDecision Policy, PublicMarkdownBodyResult Outcome, string InputKey, AgentReadyHeadData Head)
    {
        internal string NodeId => "agent-ready:page:" + Html.RelativeOutputPath;
        internal string OutputIdentity => ComputeTextContentSha256(JsonSerializer.Serialize(new
        {
            Outcome.Status,
            Body = Outcome.Body is null ? null : ComputeTextContentSha256(Outcome.Body),
            Markdown = Markdown?.RelativeOutputPath,
            Head,
        }));
    }

    private async Task PrepareAgentReadyAsync(RenderContext configuration, IReadOnlyList<MarkdownPost> posts,
        IReadOnlyList<IntegratedContentPage> contentPages, SiteGenerationOptions options, SiteBuildCache cache,
        IReadOnlyList<BuildNode> collectionNodes, IReadOnlyList<BuildNode> assetNodes,
        IReadOnlyList<BuildInput> componentInputs, ISiteTemplate template, CancellationToken token)
    {
        if (configuration.AgentReady is not { } state)
            return;
        state.Preparing = true;
        try
        {
            var routes = configuration.Routes;
            var sourcePages = posts.Select(post => new KeyValuePair<string, SiteRoute>(post.FilePath, routes.Post(post)))
                .Concat(contentPages.Select(page => new KeyValuePair<string, SiteRoute>(page.MarkdownSourcePath ?? page.SourcePath, page.Route)))
                .Concat(configuration.ExtraPages.Select(page => new KeyValuePair<string, SiteRoute>("extra:" + routes.ExtraPage(page).RelativeOutputPath, routes.ExtraPage(page)))).ToArray();
            var publicRoutes = template.GetType() == typeof(DocsSiteTemplate) || template is BlogSiteTemplate
                ? BuiltInSiteTemplateBuildPlanAdapter.CreateTemplateOwnerMap(template, posts, configuration.ExtraPages, routes)
                    // The ownership map includes the optional theme asset even when no render action exists.
                    .Keys.Where(path => path != routes.ThemeRestorationScript.RelativeOutputPath || HasExternalThemeRestoration(configuration))
                    .Select(path => routes.File(EscapeOutputPath(path)))
                : Enumerable.Empty<SiteRoute>();
            publicRoutes = publicRoutes.Concat(GetCommonArtifactRoutes(configuration, state.GenerateLlms))
                .Concat(state.Options.Robots.Mode == AgentReadyRobotsMode.Generated ? [routes.File("robots.txt")] : [])
                .Concat(assetNodes.SelectMany(node => node.Artifacts).Select(artifact => routes.File(EscapeOutputPath(artifact.RelativeOutputPath))))
                .Concat(configuration.ScopedSearch.Select(projection => routes.File(EscapeOutputPath(projection.RelativeOutputPath))))
                .Concat(options.Redirects.Select(redirect => redirect.Source.WithBaseUrl(configuration.Site.BaseUrl)))
                .ToArray();
            var sourceAssets = configuration.Assets.RegisteredRoutes.Select(asset => new KeyValuePair<string, SiteRoute>(
                Path.GetFullPath(Path.Combine(asset.Asset.InputRoot, asset.Asset.RelativeInputPath)), asset.Route)).ToList();
            if (options.PublicDirectory is { } publicDirectory)
                sourceAssets.AddRange(configuration.Assets.Files.Select(file => new KeyValuePair<string, SiteRoute>(
                    Path.GetFullPath(Path.Combine(publicDirectory, file.RelativePath)),
                    SiteRoute.ForFile(EscapeOutputPath(file.RelativePath), configuration.Site.BaseUrl))));
            var sourceMaps = posts.Select(post => post.BodySource).Concat(contentPages.Select(page => page.BodySource));
            var includedSources = sourceMaps.Where(source => source?.SourceMap is not null)
                .SelectMany(source => source!.SourceMap!.Map(new(0, source.SourceMap.Output.Text.Length), token).Origins)
                .Select(origin => origin.Source.SourceId).Distinct(StringComparer.Ordinal).ToArray();
            var resolver = new PublicMarkdownUrlResolver(configuration.Site.BaseUrl, sourcePages, sourceAssets, includedSources, publicRoutes);
            var collectionInputs = collectionNodes.ToDictionary(node => node.Id.Value, node => node.Inputs, StringComparer.Ordinal);
            var catalogIdentity = ComputeTextContentSha256(JsonSerializer.Serialize(new
            {
                Pages = sourcePages.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new { Source = pair.Key, pair.Value.PublicPath }),
                Assets = sourceAssets.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new { Source = pair.Key, pair.Value.PublicPath }),
                AssetInputs = assetNodes.Select(node => new { node.Id, node.Inputs }),
                PublicRoutes = publicRoutes.Select(route => new { route.PublicPath, route.RelativeOutputPath }),
            }));
            state.GlobalIdentity = ComputeTextContentSha256(JsonSerializer.Serialize(new { state.Options, state.GenerateLlms, configuration.Site.BaseUrl }));
            var plans = new List<AgentReadyPlannedPage>();
            var warnings = new List<SiteDiagnostic>();
            foreach (var post in posts)
            {
                token.ThrowIfCancellationRequested();
                var metadata = LegacyPageAdapters.ToPageMetadata(post);
                var route = routes.Post(post);
                var key = Key(metadata, route, new
                {
                    Body = MarkdownDocumentFingerprints.ExactBodyCacheHash(post.MarkdownBody),
                    post.FilePath,
                    Origins = MarkdownOriginIdentity(post.BodySource)
                });
                await AddAsync(route, metadata, false, true, key, true, post.FilePath, post.BodySource, () =>
                {
                    var compiled = CompilePostBody(configuration, post).SourceCompilation;
                    return compiled is null ? new(PublicMarkdownStatus.Unavailable, null, "The authoritative body compilation is unavailable.")
                        : Convert(compiled.Value, post.BodySource, route);
                });
            }
            foreach (var page in contentPages)
            {
                token.ThrowIfCancellationRequested();
                var key = Key(page.Metadata, page.Route, new
                {
                    page.SourceFingerprint,
                    Inputs = collectionInputs.GetValueOrDefault(page.OwnerId),
                    Origins = MarkdownOriginIdentity(page.BodySource)
                });
                await AddAsync(page.Route, page.Metadata, page.IsUnlisted, page.IsIncludedIn(GeneratedPageDerivedSurfaces.LlmsTxt),
                    key, page.CanCacheRendering, page.MarkdownSourcePath ?? page.SourcePath, page.BodySource, () =>
                {
                    var provided = page.GetPublicMarkdown(this, configuration, configuration.EnvironmentName, configuration.Assets, token);
                    return provided.Status switch
                    {
                        ContentPageMarkdownStatus.Provided => Convert(page.CompilePublicMarkdown(provided.Markdown!),
                            provided.BodySource.CanMapBody(provided.Markdown!, provided.SourcePath) ? provided.BodySource : null, page.Route,
                            allowSourceRelative: provided.BodySource?.SourceMap is null || provided.BodySource.CanMapBody(provided.Markdown!, provided.SourcePath)),
                        ContentPageMarkdownStatus.Failed => new(PublicMarkdownStatus.Failed, null, "The typed public Markdown provider failed."),
                        ContentPageMarkdownStatus.Unavailable => new(PublicMarkdownStatus.Unavailable, null, "The page has no verified complete public Markdown source."),
                        _ => new(PublicMarkdownStatus.Unsupported, null, "The page has no complete public Markdown provider."),
                    };
                });
            }
            foreach (var extra in configuration.ExtraPages)
            {
                var metadata = LegacyPageAdapters.ToSitePage(extra, configuration.Site.BaseUrl).Metadata;
                var route = routes.ExtraPage(extra);
                await AddAsync(route, metadata, false, false, Key(metadata, route, extra.BodyHtml), true,
                    "extra:" + route.RelativeOutputPath, null,
                    () => new(PublicMarkdownStatus.Unsupported, null, "The HTML page has no complete public Markdown replacement."));
            }
            state.Pages = Array.AsReadOnly(plans.ToArray());
            state.ByPath = plans.ToDictionary(page => page.Html.RelativeOutputPath, StringComparer.Ordinal);
            state.ByCanonical = plans.ToDictionary(page => page.Head.CanonicalUrl, StringComparer.Ordinal);
            state.TextArtifacts = plans.Where(page => page.Markdown is not null).ToDictionary(page => page.Markdown!.RelativeOutputPath,
                page => SerializePublicMarkdown(page), StringComparer.Ordinal);
            state.Diagnostics = Array.AsReadOnly(warnings.ToArray());

            string Key(PageMetadata metadata, SiteRoute route, object source) => ComputeTextContentSha256(JsonSerializer.Serialize(new
            {
                Implementation = PublicMarkdownBody.ImplementationVersion,
                Compiler = _markdownCompiler.Fingerprint,
                Catalog = catalogIdentity,
                Site = configuration.Site,
                source,
                route,
                metadata,
                componentInputs,
                ComponentContext = _markdownCompiler.Options.Markdown.Components?.Registrations.Count > 0
                    ? JsonSerializer.Serialize(new { configuration.Text, configuration.Theme, configuration.BuildTimestamp }) : null,
                configuration.EnvironmentName,
            }));
            PublicMarkdownBodyResult Convert(MarkdownCompilationResult compiled, DocumentSource? source, SiteRoute route, bool allowSourceRelative = true) =>
                PublicMarkdownBody.Convert(compiled, _markdownCompiler.Options.Markdown, source,
                    link => !allowSourceRelative && IsSourceRelative(link.Target) ? null : resolver.Resolve(link.Target, link.SourceId, route), token);
            static bool IsSourceRelative(string target) => !target.StartsWith('/') && !target.StartsWith('#')
                && !target.StartsWith('?') && !Uri.TryCreate(target, UriKind.Absolute, out _);
            async Task AddAsync(SiteRoute route, PageMetadata metadata, bool unlisted, bool llms, string key,
                bool cacheable, string sourceId, DocumentSource? source, Func<PublicMarkdownBodyResult> create)
            {
                var policy = AgentReadyPolicy.Evaluate(state.Options, metadata, unlisted, llms,
                    configuration.BuildTimestamp, configuration.EnvironmentName);
                PublicMarkdownBodyResult outcome;
                SiteRoute? markdown = null;
                if (!policy.ShouldProvideMarkdown)
                    outcome = new(policy.InitialStatus, null, null);
                else
                {
                    outcome = cacheable ? await cache.ReadPublicMarkdownAsync(key, token).ConfigureAwait(false) ?? await ProduceAsync() : await ProduceAsync();
                    if (outcome.IsComplete)
                    {
                        markdown = AgentReadyPolicy.MarkdownRoute(route, metadata.AgentReady, configuration.Site.BaseUrl);
                        if (markdown is null)
                            outcome = new(PublicMarkdownStatus.Unsupported, null, "The HTML route requires an explicit Markdown route.");
                    }
                    if (!outcome.IsComplete)
                    {
                        var diagnostic = new SiteDiagnostic("LSQ012", policy.RequiresMarkdown ? SiteDiagnosticSeverity.Error : SiteDiagnosticSeverity.Warning,
                            "Public Markdown is unavailable: " + outcome.Reason, new(route.RelativeOutputPath), "AgentReady", null);
                        if (policy.RequiresMarkdown)
                            throw new SiteQualityValidationException(new SiteQualityReport([diagnostic]));
                        warnings.Add(diagnostic);
                    }
                }
                var title = metadata.Title ?? route.PublicPath;
                var head = new AgentReadyHeadData
                {
                    SiteTitle = configuration.Site.Title,
                    SiteUrl = configuration.Site.BaseUrl,
                    SiteLanguage = configuration.Site.Language,
                    CanonicalUrl = routes.AbsoluteUrl(route),
                    Title = title == configuration.Site.Title ? title : title + " - " + configuration.Site.Title,
                    Description = string.IsNullOrWhiteSpace(metadata.Description) ? configuration.Site.Description : metadata.Description,
                    Language = metadata.Language ?? configuration.Site.Language,
                    Page = metadata.AgentReady.StructuredData,
                    MarkdownUrl = markdown is null ? null : routes.AbsoluteUrl(markdown),
                    LlmsUrl = state.GenerateLlms && policy.ShouldIndex ? routes.AbsoluteUrl(routes.Llms) : null,
                };
                plans.Add(new(route, markdown, metadata, policy, outcome, key, head));

                async Task<PublicMarkdownBodyResult> ProduceAsync()
                {
                    PublicMarkdownBodyResult produced;
                    if (metadata.AgentReady.MarkdownReplacement is { } replacement)
                    {
                        var replacementSource = new DocumentSource(sourceId, 0);
                        var compiled = _markdownCompiler.Analyze(replacement, replacementSource, token, null, CreateMarkdownComponentContext(configuration));
                        produced = Convert(compiled, replacementSource, route);
                        if (produced.IsComplete)
                            produced = produced with { Status = PublicMarkdownStatus.ExplicitReplacement };
                    }
                    else
                        produced = create();
                    if (cacheable)
                    {
                        try
                        {
                            await cache.StorePublicMarkdownAsync(key, produced, token).ConfigureAwait(false);
                        }
                        catch (Exception exception) when (IsOptionalCacheWriteFailure(exception)) { }
                    }
                    return produced;
                }
            }
        }
        finally
        {
            state.Preparing = false;
        }
    }

    private static SiteQualityReport AppendAgentReadyDiagnostics(SiteQualityReport report, IEnumerable<SiteDiagnostic> diagnostics) =>
        new(report.Diagnostics.Concat(diagnostics))
        {
            HtmlParseCount = report.HtmlParseCount,
            HtmlFactsCacheHitCount = report.HtmlFactsCacheHitCount,
            PublicationManifest = report.PublicationManifest,
            PublicationCompatibility = report.PublicationCompatibility,
            PublicationCompatibilityRequired = report.PublicationCompatibilityRequired,
            HtmlReferences = report.HtmlReferences,
            CssReferences = report.CssReferences,
            AnchorReferences = report.AnchorReferences,
        };

    private static string SerializePublicMarkdown(AgentReadyPlannedPage page)
    {
        // This is an explicit public allowlist, never the source front matter or cache envelope.
        var text = new StringBuilder("---\ntitle: ").Append(JsonSerializer.Serialize(page.Metadata.Title ?? page.Html.PublicPath))
            .Append("\ncanonical: ").Append(JsonSerializer.Serialize(page.Head.CanonicalUrl));
        if (!string.IsNullOrWhiteSpace(page.Head.Description))
            text.Append("\ndescription: ").Append(JsonSerializer.Serialize(page.Head.Description));
        if (!string.IsNullOrWhiteSpace(page.Head.Language))
            text.Append("\nlanguage: ").Append(JsonSerializer.Serialize(page.Head.Language));
        return text.Append("\n---\n\n").Append(page.Outcome.Body).ToString();
    }

    private static IEnumerable<BuildNode> CreateAgentReadyNodes(AgentReadyBuildState state)
    {
        foreach (var page in state.Pages)
        {
            var owner = new BuildNodeId(page.NodeId);
            yield return new(owner, [BuildInput.FromConfiguration("agent-ready.input", page.InputKey),
                BuildInput.FromConfiguration("agent-ready.output", page.OutputIdentity)], artifacts:
                page.Markdown is null ? [] : [new(new("artifact:" + page.NodeId), owner, page.Markdown.RelativeOutputPath)]);
        }
        if (state.TextArtifacts.TryGetValue("robots.txt", out var robots))
        {
            var owner = new BuildNodeId("agent-ready:robots");
            yield return new(owner, [BuildInput.FromConfiguration("agent-ready.output", ComputeTextContentSha256(robots))],
                artifacts: [new(new("artifact:agent-ready:robots"), owner, "robots.txt")]);
        }
    }

    private static string AgentReadyLlms(RenderContext configuration)
    {
        var builder = new StringBuilder("# ").Append(AgentReadyLlmsFormatting.Heading(configuration.Site.Title)).Append('\n');
        if (!string.IsNullOrWhiteSpace(configuration.Site.Description))
            builder.Append("\n> ").Append(AgentReadyLlmsFormatting.Description(configuration.Site.Description)).Append('\n');
        builder.Append("\n## ").Append(AgentReadyLlmsFormatting.Heading(configuration.Text.LlmsPostsHeading)).Append("\n\n");
        foreach (var page in configuration.AgentReady!.Pages.Where(page => page.Policy.ShouldIndex))
            builder.Append(AgentReadyLlmsFormatting.Entry(new(page.Head.CanonicalUrl, page.Head.MarkdownUrl,
                page.Metadata.Title ?? page.Html.PublicPath, page.Metadata.Description)));
        return builder.ToString();
    }

    private static async Task ValidateAgentReadyScopeAsync(SiteBuildPlan fullPlan, AgentReadyBuildState? state,
        Func<string, bool> inScope, SiteBuildCache cache, OutputTransaction transaction, CancellationToken token)
    {
        var previous = await cache.LoadAsync(await transaction.ReadPreviousBuildCacheKeyAsync(token).ConfigureAwait(false), token).ConfigureAwait(false);
        var previousGlobal = previous.Values.SelectMany(node => node.Inputs).FirstOrDefault(input => input.Key == "agent-ready.site")?.Value;
        if (previousGlobal != state?.GlobalIdentity)
            throw new ArgumentException("Changing global Agent Ready or robots/index policy requires a full build, not an OutputScope subset.");
        if (state is null)
            return;
        foreach (var old in previous.Values.Where(node => node.NodeId.StartsWith("agent-ready:page:", StringComparison.Ordinal)))
        {
            var html = old.NodeId["agent-ready:page:".Length..];
            if (state.ByPath.ContainsKey(html))
                continue;
            var related = old.Artifacts.Select(artifact => artifact.RelativePath).Append(html).Distinct(StringComparer.Ordinal).ToArray();
            if (related.Any(inScope) && related.Any(path => !inScope(path)))
                throw new ArgumentException("Removing an Agent Ready page requires both its old HTML and Markdown paths in OutputScope, or a full build.");
        }
        foreach (var page in state.Pages)
        {
            previous.TryGetValue(page.NodeId, out var old);
            var oldIdentity = old?.Inputs.FirstOrDefault(input => input.Key == "agent-ready.output")?.Value;
            if (oldIdentity == page.OutputIdentity)
                continue;
            var related = (old?.Artifacts.Select(artifact => artifact.RelativePath) ?? [])
                .Concat(page.Markdown is null ? [] : [page.Markdown.RelativeOutputPath]).Append(page.Html.RelativeOutputPath).Distinct(StringComparer.Ordinal).ToArray();
            if (related.Any(inScope) && related.Any(path => !inScope(path)))
                throw new ArgumentException("The public Markdown change requires its paired HTML/Markdown paths. Run a full build or select an enclosing directory.");
        }
        foreach (var node in fullPlan.Nodes.Where(node => node.Artifacts.Any(artifact => artifact.RelativeOutputPath == "llms.txt")))
        {
            var path = node.Artifacts.First(artifact => artifact.RelativeOutputPath == "llms.txt").RelativeOutputPath;
            if (inScope(path))
                continue;
            var current = node.Inputs.FirstOrDefault(input => input.Key == "agent-ready.llms")?.Value;
            var before = previous.GetValueOrDefault(node.Id.Value)?.Inputs.FirstOrDefault(input => input.Key == "agent-ready.llms")?.Value;
            if (current != before)
                throw new ArgumentException("This subset would change llms.txt outside OutputScope. Run a full build or include its exact path.");
        }
    }
}
