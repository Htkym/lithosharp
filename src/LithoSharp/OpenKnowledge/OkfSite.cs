using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using LithoSharp.Build;
using LithoSharp.Content;
using LithoSharp.Content.Compilation;
using LithoSharp.Pages;
using LithoSharp.Routing;
using LithoSharp.Publishing;

namespace LithoSharp.OpenKnowledge;

public sealed partial class OkfBundle
{
    /// <summary>Connects available bundle documents to existing content publication, routing and Docs navigation.</summary>
    /// <remarks>Draft, stale and unverified OKF records remain readable by default. Supply an explicit mapper and fingerprint to change publication.</remarks>
    public ContentCollection<OkfDocument, string> CreateContentCollection(ContentCollectionId collectionId,
        Func<OkfDocument, PageMetadata>? publicationMapper = null, string? policyFingerprint = null, string? outputPrefix = null)
    {
        ArgumentNullException.ThrowIfNull(collectionId);
        if (Catalog is null || !SnapshotComplete)
            throw new InvalidOperationException("Site publication requires a complete file snapshot and collision-free routes.");
        var prefix = OutputPrefix(collectionId, outputPrefix);
        var membership = Hash(string.Join('\n', Catalog.Entries.Select(static entry => entry.InputPath + ":" + entry.Kind + ":" + entry.Route.RelativeOutputPath)));
        var entries = Documents.Where(static document => document.IsAvailable && document.Body is not null).Select(document =>
        {
            var relations = Links.Where(link => link.SourcePath == document.Path || link.TargetPath == document.Path).ToArray();
            var dependencies = new List<ContentDependency>
            {
                ContentDependency.FromValue("okf:relations", Hash(JsonSerializer.Serialize(relations.Select(static link => new
                { link.SourcePath, link.OriginalTarget, link.TargetPath, link.PublicUrl, link.Kind })))),
                ContentDependency.FromValue("okf:as-of", AsOf.ToString("O", CultureInfo.InvariantCulture)),
            };
            foreach (var target in relations.Select(link => link.SourcePath == document.Path ? link.TargetPath : link.SourcePath)
                .Where(static path => path is not null)
                .Select(path => filesByPath.GetValueOrDefault(ContentInputPolicy.NormalizePath(path!)))
                .Where(static file => file is not null).Distinct())
                dependencies.Add(ContentDependency.FromCapturedFile(target!.Path, target.Sha256));
            dependencies.Add(ContentDependency.FromCapturedFile(document.Path, document.File.Sha256));
            return new ContentEntry<OkfDocument, string>(new ContentEntryId(document.Path), document.Path, "sha256:" + document.File.Sha256,
                document, document.Body!, new(document.Path, 1, 1))
            {
                InputProfile = ContentInputProfile.OpenKnowledgeFormat,
                InputKind = document.Kind,
                InputRelations = Array.AsReadOnly(Links.Where(link => link.SourcePath == document.Path).ToArray()),
                DeclaredDependencies = dependencies,
                CapturedInputs = [new(Path.Combine(InputRoot, document.Path.Replace('/', Path.DirectorySeparatorChar)), document.File.Sha256)],
                BodySource = new DocumentSource(document.Path, document.Syntax!.Analysis.BodySpan!.Value.Start)
                { BodyStartLine = document.Syntax.Analysis.GetLinePosition(document.Syntax.Analysis.BodySpan.Value.Start).Line },
            };
        }).ToArray();
        var publication = entries.ToDictionary(static entry => entry.Id, entry => publicationMapper is null ? new PageMetadata(
                entry.FrontMatter.Metadata?.Title ?? entry.FrontMatter.ConceptId ?? entry.FrontMatter.Path,
                entry.FrontMatter.Metadata?.Description)
                : publicationMapper(entry.FrontMatter) ?? throw new InvalidOperationException("The explicit OKF publication mapper returned null."));
        return new ContentCollection<OkfDocument, string>(collectionId, InputRoot, entries,
            entry => PublicRoute(entry.SourcePath, prefix), entry => publication[entry.Id],
            declaredDependencies:
            [
                ContentDependency.FromValue("okf:membership", membership),
                ContentDependency.FromValue("okf:parser", string.Join(';', Documents.Select(static document =>
                    document.Syntax?.Analysis.ParserVersion + ":" + document.Syntax?.Analysis.OptionsHash).Distinct(StringComparer.Ordinal))),
                ContentDependency.FromValue("okf:publication-policy", policyFingerprint ?? (publicationMapper is null ? "default-readable/1" : "custom-unfingerprinted")),
                ContentDependency.FromValue("okf:output-prefix", prefix),
            ], transformationId: new ContentTransformationId("okf-v0.2/1"),
            isCacheable: publicationMapper is null || !string.IsNullOrWhiteSpace(policyFingerprint));
    }

    public SiteContentCollection<OkfDocument, string> RenderDocs(ContentCollectionId collectionId,
        Func<OkfDocument, PageMetadata>? publicationMapper = null, string? policyFingerprint = null, string? outputPrefix = null)
    {
        var prefix = OutputPrefix(collectionId, outputPrefix);
        var collection = CreateContentCollection(collectionId, publicationMapper, policyFingerprint, prefix);
        return collection.RenderDocs(entry => new DocsPagePresentation
        {
            Title = entry.FrontMatter.Metadata?.Title ?? entry.FrontMatter.ConceptId ?? entry.FrontMatter.Path,
            Description = entry.FrontMatter.Metadata?.Description,
        }, (entry, context) => RenderMetadata(entry.FrontMatter) + RenderBody(entry.FrontMatter, context, prefix, collection),
            rendererFingerprint: "okf-docs/1", isThreadSafe: true);
    }

    /// <summary>Publishes captured auxiliary files through the existing transactional asset pipeline without image transforms.</summary>
    public IReadOnlyList<SiteGeneratedAsset> CreateAuxiliaryAssets(string outputPrefix)
        => CreateAuxiliaryAssets(outputPrefix, null);

    private IReadOnlyList<SiteGeneratedAsset> CreateAuxiliaryAssets(string outputPrefix, ContentCollectionId? collectionId)
    {
        ArgumentNullException.ThrowIfNull(outputPrefix);
        if (outputPrefix.Length != 0)
            _ = ContentInputPolicy.NormalizePath(outputPrefix);
        return Array.AsReadOnly(Files.Where(static file => file.Kind == ContentInputKind.Auxiliary)
            .Select(file => new SiteGeneratedAsset("okf:aux:" + Hash(outputPrefix + ":" + file.Path), PublicRoute(file.Path, outputPrefix).RelativeOutputPath, file.Bytes,
                [BuildInput.FromFile(file.Path, file.Sha256), BuildInput.FromConfiguration("okf:preserved-auxiliary", "1"),
                    BuildInput.FromValue("content.inputRoot", InputRoot), BuildInput.FromValue("content.sourcePath", file.Path),
                    BuildInput.FromValue("content.collection", collectionId?.Value ?? string.Empty),
                    BuildInput.FromValue("content.inputProfile", ContentInputProfile.OpenKnowledgeFormat.ToString()),
                    BuildInput.FromValue("content.inputKind", ContentInputKind.Auxiliary.ToString())])).ToArray());
    }

    public SiteGenerationOptions CreateSiteOptions(ContentCollectionId collectionId,
        Func<OkfDocument, PageMetadata>? publicationMapper = null, string? policyFingerprint = null, string? outputPrefix = null) => new()
        {
            Markdown = new MarkdownOptions { ProfileId = MarkdownOptions.DocumentationProfile },
            ContentCollections = [RenderDocs(collectionId, publicationMapper, policyFingerprint, outputPrefix)],
            GeneratedAssets = CreateAuxiliaryAssets(OutputPrefix(collectionId, outputPrefix), collectionId),
        };

    private string RenderBody(OkfDocument document, ContentPageRenderingContext context, string outputPrefix, ContentCollection<OkfDocument, string> collection)
    {
        var compiler = new LithoMarkdownCompiler(MarkdownCompilerOptions.Default with
        { Markdown = new MarkdownOptions { ProfileId = MarkdownOptions.DocumentationProfile } });
        var bodyStart = document.Syntax!.Analysis.BodySpan!.Value.Start;
        var compiled = compiler.AnalyzeProjected(document.Body!, new DocumentSource(document.Path, bodyStart)
        { BodyStartLine = document.Syntax.Analysis.GetLinePosition(bodyStart).Line }, context.RenderCancellation, (target, image) =>
        {
            var link = Links.FirstOrDefault(link => link.SourcePath == document.Path && link.Syntax.Image == image && link.OriginalTarget == target);
            if (link is null)
                return null;
            if (!link.IsResolved)
                return new MarkdownLinkProjection(null, target);
            if (link.Kind == OkfLinkKind.External || link.TargetPath is null)
                return new MarkdownLinkProjection(link.PublicUrl, target);
            if (link.Kind != OkfLinkKind.Auxiliary)
            {
                var entry = collection.Entries.FirstOrDefault(entry => ContentInputPolicy.PathComparer.Equals(entry.SourcePath, ContentInputPolicy.NormalizePath(link.TargetPath)));
                if (entry is null || !PagePublicationPolicy.ShouldPublish(collection.PublicationMapper(entry), context.BuildTimestamp, context.EnvironmentName))
                    return new MarkdownLinkProjection(null, target);
            }
            var suffix = link.PublicUrl![Catalog!.GetInput(ContentInputPolicy.NormalizePath(link.TargetPath)).Route.PublicPath.Length..];
            return new MarkdownLinkProjection(PublicRoute(link.TargetPath, outputPrefix).WithBaseUrl(context.Site.BaseUrl).PublicPath + suffix, target);
        });
        return context.AcceptProjectedMarkdown(compiled) + RenderBacklinks(document, context, outputPrefix, collection);
    }

    private string RenderBacklinks(OkfDocument document, ContentPageRenderingContext context, string outputPrefix, ContentCollection<OkfDocument, string> collection)
    {
        var html = new StringBuilder();
        foreach (var path in Links.Where(link => link.IsConceptEdge && link.TargetPath == document.Path)
            .Select(static link => link.SourcePath).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var entry = collection.Entries.FirstOrDefault(entry => entry.FrontMatter.Path == path);
            if (entry is null || !PagePublicationPolicy.ShouldPublish(collection.PublicationMapper(entry), context.BuildTimestamp, context.EnvironmentName))
                continue;
            if (html.Length == 0)
                html.Append("<aside class=\"okf-backlinks\"><h2>Referenced by</h2><ul>");
            html.Append("<li><a href=\"").Append(WebUtility.HtmlEncode(PublicRoute(path, outputPrefix).WithBaseUrl(context.Site.BaseUrl).PublicPath))
                .Append("\">").Append(WebUtility.HtmlEncode(entry.FrontMatter.Metadata?.Title ?? entry.FrontMatter.ConceptId ?? path)).Append("</a></li>");
        }
        return html.Length == 0 ? string.Empty : html.Append("</ul></aside>").ToString();
    }

    private SiteRoute PublicRoute(string path, string outputPrefix)
    {
        var entry = Catalog?.GetInput(ContentInputPolicy.NormalizePath(path))
            ?? throw new InvalidOperationException("Site publication requires collision-free routes.");
        var prefix = outputPrefix.Length == 0 ? string.Empty : ContentInputPolicy.NormalizePath(outputPrefix) + "/";
        return SiteRoute.ForFile(string.Join('/', (prefix + entry.Route.RelativeOutputPath).Split('/').Select(Uri.EscapeDataString)));
    }

    private static string OutputPrefix(ContentCollectionId collectionId, string? outputPrefix)
    {
        ArgumentNullException.ThrowIfNull(collectionId);
        var prefix = outputPrefix ?? collectionId.Value;
        return prefix.Length == 0 ? string.Empty : ContentInputPolicy.NormalizePath(prefix);
    }

    private static string RenderMetadata(OkfDocument document)
    {
        if (document.Metadata is not { } metadata)
            return string.Empty;
        var html = new StringBuilder("<aside class=\"okf-metadata\" aria-label=\"Knowledge metadata\"><dl>");
        Item("Type", metadata.Type);
        Item("Status", metadata.Status);
        Item("Trust record", metadata.TrustTier.ToString());
        Item("Verification records complete", metadata.VerificationIsComplete.ToString());
        Item("Freshness", metadata.Freshness.ToString());
        Item("Tags", metadata.Tags.Count == 0 ? null : string.Join(", ", metadata.Tags));
        Item("Resource", metadata.Resource);
        Item("Generated by", metadata.GeneratedBy);
        if (metadata.DisplayChangedAt is { } changedAt)
            Item(metadata.Generated is null ? "Legacy timestamp" : "Generated at", changedAt.ToString("O", CultureInfo.InvariantCulture));
        html.Append("</dl><p>Trust records describe declared verification; they do not establish identity or factual accuracy.</p>");
        if (metadata.Sources.Count != 0)
        {
            html.Append("<ul class=\"okf-sources\">");
            foreach (var source in metadata.Sources)
                html.Append("<li data-okf-source-id=\"").Append(Encode(source.Id)).Append("\"><strong>")
                    .Append(Encode(source.Title ?? source.Id)).Append("</strong> ").Append(Encode(source.Resource)).Append("</li>");
            html.Append("</ul>");
        }
        foreach (var verification in metadata.VerificationEvents)
            html.Append("<p class=\"okf-verification\">").Append(Encode(verification.Actor)).Append(" ")
                .Append(Encode(verification.At?.ToString("O", CultureInfo.InvariantCulture))).Append("</p>");
        foreach (var field in new[] { "runtime", "parameters", "computation", "executor", "attester" })
            if (metadata.GetField(field) is { } node)
                html.Append("<details><summary>").Append(field).Append("</summary><pre>").Append(Encode(node.GetRawText())).Append("</pre></details>");
        return html.Append("</aside>").ToString();
        void Item(string label, string? value)
        {
            if (value is not null)
                html.Append("<dt>").Append(label).Append("</dt><dd>").Append(Encode(value)).Append("</dd>");
        }
        static string Encode(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
    }
}
