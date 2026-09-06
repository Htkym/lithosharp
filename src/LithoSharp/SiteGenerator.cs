using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using System.Xml;
using LithoSharp.Build;
using LithoSharp.Compatibility;
using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Diagnostics;
using LithoSharp.Pages;
using LithoSharp.Publishing;
using LithoSharp.Quality;
using LithoSharp.Routing;
using LithoSharp.Search;
using LithoSharp.Validation;
using Markdig;

namespace LithoSharp;

/// <summary>
/// A general-purpose generator that builds a static site from Markdown posts and
/// <see cref="SiteSettings"/>. Text, theme, content validation, and extra pages are
/// swapped in through <see cref="SiteCustomization"/>.
/// </summary>
public sealed partial class SiteGenerator
{
    private readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();
    private const string OutputManifestRelativePath = ".lithosharp-output-manifest.json";
    private const int OutputManifestVersion = 1;
    private const int OutputOwnershipStateVersion = 1;
    internal const string SocialImageSourceFileName = "android-chrome-192x192.png";
    private static readonly (string FileName, string Sizes)[] PngFaviconAssets =
    [
        ("favicon-16x16.png", "16x16"),
        ("favicon-32x32.png", "32x32"),
        ("favicon-48x48.png", "48x48"),
        ("favicon-64x64.png", "64x64"),
        ("favicon-96x96.png", "96x96"),
        ("favicon-128x128.png", "128x128"),
        ("favicon-180x180.png", "180x180"),
        ("favicon-192x192.png", "192x192"),
        ("favicon-256x256.png", "256x256"),
        ("favicon-512x512.png", "512x512")
    ];
    private static readonly (string FileName, string Sizes)[] ManifestIconAssets =
    [
        ("android-chrome-192x192.png", "192x192"),
        ("android-chrome-512x512.png", "512x512")
    ];
    private static readonly string[] BundledFaviconAssets =
    [
        "favicon.ico",
        "apple-touch-icon.png",
        "android-chrome-192x192.png",
        "android-chrome-512x512.png",
        .. PngFaviconAssets.Select(static asset => asset.FileName)
    ];
    internal static IReadOnlyList<string> BundledFaviconAssetNames => BundledFaviconAssets;
    private static readonly JsonSerializerOptions SearchSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };
    private static readonly JsonSerializerOptions WebManifestSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };
    private static readonly JsonSerializerOptions SearchMessageSerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly SearchJsonSerializerContext SearchSerializerContext = new(SearchSerializerOptions);
    private static readonly Regex BodyHeadingOneOpenRegex = new("<h1(?<attributes>\\s[^>]*)?>", RegexOptions.Singleline);
    private static readonly Regex BodyHeadingOneCloseRegex = new("</h1>", RegexOptions.Singleline);
    private static readonly Regex HeadingRegex = new("<h(?<level>[2-3]) id=\"(?<id>[^\"]+)\">(?<text>.*?)</h\\k<level>>", RegexOptions.Singleline);
    private static readonly Regex AnchorTagRegex = new("<a(?<before>[^>]*?)\\shref=\"(?<href>[^\"]+)\"(?<after>[^>]*)>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private const int SearchIndexMaxLength = 12000;
    private static readonly Regex StripTagsRegex = new("<.*?>", RegexOptions.Singleline);

    internal sealed record RenderContext(
        SiteSettings Site,
        SiteText Text,
        SiteThemeOptions Theme,
        IReadOnlyList<SiteExtraPage> ExtraPages,
        IReadOnlyList<IntegratedContentPage> ContentPages,
        string FaviconSourceDirectory,
        bool HasFaviconAssets,
        bool HasSocialImage,
        DateTimeOffset BuildTimestamp,
        SiteRouteCatalog Routes);

    private sealed record PageClaim<TContent>(
        SitePage<TContent> Page,
        string OwnerId,
        SiteSourceLocation? SourceLocation)
        where TContent : notnull;

    private sealed record GeneratedArtifactState(
        string Path,
        string Sha256);

    private sealed record OutputOwnershipState(
        string OutputIdentity,
        IReadOnlyList<GeneratedArtifactState> Artifacts);


    /// <summary>Generates a static site from the site settings and posts.</summary>
    /// <param name="site">Site settings.</param>
    /// <param name="posts">Posts to render.</param>
    /// <param name="outputDirectory">Output directory.</param>
    /// <param name="clean">Whether to delete the output directory before generating.</param>
    /// <param name="customization">Text, theme, extra pages, and other overrides. Defaults to English when omitted.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The generation result.</returns>
    /// <remarks>
    /// Cooperating processes are serialized by a Unicode-normalized, case-folded output identity.
    /// Transaction metadata is owner-only where supported. Symbolic links and name-surrogate
    /// reparse points are rejected. Because .NET has no portable handle-relative no-follow directory
    /// traversal, hostile same-user mutation outside this lock protocol is unsupported.
    /// Generator ownership and content fingerprints are kept in an owner-restricted sibling sidecar.
    /// The manifest published inside the output tree is informational and is never used for deletion.
    /// With <paramref name="clean"/> set to <see langword="false"/>, timestamps, file attributes,
    /// Windows owner/group/DACL data, and Unix permission modes are preserved. Unix ownership,
    /// ACLs, extended attributes, and other platform metadata are not promised.
    /// If cleanup after promotion fails, generation still succeeds, a trace warning is emitted,
    /// and the owned backup is retained for cleanup by a later generation.
    /// </remarks>
    public Task<SiteGenerationResult> GenerateAsync(
        SiteSettings site,
        IReadOnlyList<MarkdownPost> posts,
        string outputDirectory,
        bool clean,
        SiteCustomization? customization = null,
        CancellationToken cancellationToken = default) =>
        GenerateWithOptionsAsync(
            site,
            posts,
            outputDirectory,
            clean,
            customization,
            new SiteGenerationOptions(),
            cancellationToken);

    /// <summary>生成オプションを指定して静的サイトを生成します。</summary>
    /// <param name="site">サイト設定。</param>
    /// <param name="posts">描画する投稿。</param>
    /// <param name="outputDirectory">出力ディレクトリ。</param>
    /// <param name="clean">生成前に出力ディレクトリを削除するかどうか。</param>
    /// <param name="customization">文言、テーマ、追加ページなどのカスタマイズ。</param>
    /// <param name="options">今回の生成に適用するオプション。</param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    /// <returns>生成結果。</returns>
    /// <remarks>
    /// Unicode 正規化と大文字化を適用した出力識別子ごとに、協調するプロセス間の生成を直列化します。
    /// 対応する環境ではトランザクション用メタデータを所有者専用にします。
    /// シンボリックリンクと名前を置き換える再解析ポイントは拒否します。
    /// 生成物の所有情報と内容フィンガープリントは、所有者専用の隣接サイドカーに保持します。
    /// 出力ツリー内に公開するマニフェストは情報提供専用であり、削除判断には使用しません。
    /// .NET には移植可能なハンドル相対の no-follow ディレクトリ走査がないため、
    /// この排他制御を使わずに同じユーザーの外部プロセスが生成中のツリーを変更する敵対的操作はサポートしません。
    /// <paramref name="clean"/> が <see langword="false"/> の場合は、タイムスタンプ、ファイル属性、
    /// Windows の所有者、グループ、DACL、および Unix のパーミッションモードを保持します。
    /// Unix の所有者、ACL、拡張属性などのメタデータ保持は保証しません。
    /// 昇格後のバックアップ削除に失敗した場合も生成は成功として扱い、トレース警告を出力して、
    /// 所有するバックアップを次回の生成で再度削除するために保持します。
    /// </remarks>
    public async Task<SiteGenerationResult> GenerateWithOptionsAsync(
        SiteSettings site,
        IReadOnlyList<MarkdownPost> posts,
        string outputDirectory,
        bool clean,
        SiteCustomization? customization,
        SiteGenerationOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(site);
        ArgumentNullException.ThrowIfNull(posts);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxDegreeOfParallelism, 1);
        if (options.EnvironmentName is null)
        {
            throw new ArgumentNullException(nameof(options.EnvironmentName));
        }

        if (string.IsNullOrWhiteSpace(options.EnvironmentName))
        {
            throw new ArgumentException(
                "The environment name must not be empty.",
                nameof(options.EnvironmentName));
        }

        if (options.ContentCollections is null)
        {
            throw new ArgumentNullException(nameof(options.ContentCollections));
        }

        if (options.Assets is null)
        {
            throw new ArgumentNullException(nameof(options.Assets));
        }

        ArgumentNullException.ThrowIfNull(options.Redirects);
        var redirectDeclarations = options.Redirects.ToArray();
        if (redirectDeclarations.Any(redirect => redirect is null))
            throw new ArgumentException("Redirects must not contain null entries.", nameof(options.Redirects));
        var outputRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
        var buildCacheRoot = Path.GetFullPath(options.BuildCacheDirectory
            ?? Path.Combine(Path.GetDirectoryName(outputRoot)!, ".lithosharp"));
        if (ContainsDirectory(outputRoot, buildCacheRoot) || ContainsDirectory(buildCacheRoot, outputRoot)
            || options.PublicDirectory is { } publicInput && ContainsDirectory(Path.GetFullPath(publicInput), buildCacheRoot))
            throw new ArgumentException("The build cache must not overlap output or be inside public input.", nameof(options));
        foreach (var asset in options.Assets)
        {
            ArgumentNullException.ThrowIfNull(asset);
            if (ContainsDirectory(outputRoot, Path.GetFullPath(Path.Combine(asset.InputRoot, asset.RelativeInputPath))))
                throw new ArgumentException("Asset input files must be outside the output directory.", nameof(options));
        }
        if (options.Quality?.ExternalLinks is { } external)
        {
            var cachePath = Path.GetFullPath(external.CacheFilePath);
            if (ContainsDirectory(outputRoot, cachePath))
                throw new ArgumentException("The external link cache must be outside the site output directory.", nameof(options.Quality));
        }

        customization ??= new SiteCustomization();
        if (options.AssetCacheDirectory is { } assetCacheDirectory)
        {
            var cachePath = Path.GetFullPath(assetCacheDirectory);
            if (ContainsDirectory(outputRoot, cachePath))
                throw new ArgumentException("The asset cache must be outside the output directory.", nameof(options));
        }
        if (options.PublicDirectory is { } publicDirectory)
        {
            var publicRoot = Path.GetFullPath(publicDirectory);
            if (ContainsDirectory(publicRoot, outputRoot) || ContainsDirectory(outputRoot, publicRoot))
                throw new ArgumentException("The public input directory and output directory must not overlap.", nameof(options));
            if (options.AssetCacheDirectory is { } cache && ContainsDirectory(publicRoot, Path.GetFullPath(cache)))
                throw new ArgumentException("The asset cache must be outside the public input directory.", nameof(options));
            if (options.Quality?.ExternalLinks is { } links && ContainsDirectory(publicRoot, Path.GetFullPath(links.CacheFilePath)))
                throw new ArgumentException("The external link cache must be outside the public input directory.", nameof(options));
        }
        var assetRegistry = await AssetRegistry.CreateAsync(options.Assets, options.AssetTransforms,
            options.PublicDirectory, options.AssetCacheDirectory, site.BaseUrl, cancellationToken).ConfigureAwait(false);
        var template = customization.Template
            ?? throw new InvalidOperationException("Site customization must specify a template.");
        var buildTimestamp = ResolveBuildTimestamp(options.BuildTimestamp);
        var pageRouteTable = new SiteRouteTable();
        var unpublishedPages = new List<string>();
        var publishedMarkdownClaims = AdaptPublishedMarkdownPages(
            posts,
            site.BaseUrl,
            buildTimestamp,
            options.EnvironmentName,
            pageRouteTable,
            unpublishedPages);
        var extraPageClaims = AdaptExtraPages(customization.ExtraPages, site.BaseUrl, pageRouteTable);
        var publishedExtraPageClaims = FilterPublished(
            extraPageClaims,
            buildTimestamp,
            options.EnvironmentName);
        var contentPages = AdaptContentCollections(
            options.ContentCollections,
            site.BaseUrl,
            buildTimestamp,
            options.EnvironmentName,
            pageRouteTable,
            unpublishedPages,
            cancellationToken);
        var builtInTemplate = template is DocsSiteTemplate or BlogSiteTemplate;
        RegisterPageRoutes(pageRouteTable, publishedMarkdownClaims, builtInTemplate);
        RegisterPageRoutes(pageRouteTable, publishedExtraPageClaims, builtInTemplate);
        pageRouteTable.ValidateOrThrow();

        var publishedPosts = publishedMarkdownClaims
            .Select(claim => claim.Page.Content)
            .ToArray();
        var publishedExtraPages = publishedExtraPageClaims
            .Select(claim => claim.Page.Content)
            .ToArray();
        var routes = new SiteRouteCatalog(
            site.BaseUrl,
            publishedMarkdownClaims.Select(claim => claim.Page),
            publishedExtraPageClaims.Select(claim => claim.Page));
        var faviconSource = customization.FaviconSourceDirectory ?? Path.Combine(AppContext.BaseDirectory, "favicon");
        var hasFaviconAssets = BundledFaviconAssets.All(asset => File.Exists(Path.Combine(faviconSource, asset)));
        var hasSocialImage = File.Exists(Path.Combine(faviconSource, SocialImageSourceFileName));
        var configuration = new RenderContext(
            site,
            customization.Text,
            customization.Theme,
            publishedExtraPages,
            contentPages,
            faviconSource,
            hasFaviconAssets,
            hasSocialImage,
            buildTimestamp,
            routes);
        var renderedContentPages = builtInTemplate ? [] : contentPages
            .Select(page => page.Render(
                this,
                configuration,
                options.EnvironmentName,
                assetRegistry,
                cancellationToken))
            .ToArray();
        var collectionFiles = contentPages
            .Select(page => new SiteTemplateFile
            {
                RelativePath = page.Route.RelativeOutputPath,
                Content = page.Content ?? string.Empty,
            })
            .ToArray();

        var docsNavigation = BuildDocsNavigation(publishedPosts);
        var templatePages = BuildTemplatePages(configuration, docsNavigation, renderContent: !builtInTemplate);
        var templateNavigation = BuildTemplateNavigation(configuration, docsNavigation, templatePages);
        var templateContext = new SiteTemplateContext(
            this,
            site,
            publishedPosts,
            customization.Text,
            customization.Theme,
            publishedExtraPages,
            renderedContentPages,
            templatePages,
            templateNavigation,
            configuration,
            assetRegistry,
            options.EnvironmentName);
        var plannedText = builtInTemplate ? CreatePlannedTemplateRenders(templateContext, template) : null;
        var templateResult = builtInTemplate
            ? new SiteTemplateResult(plannedText!.Keys.Select(path => new SiteTemplateFile { RelativePath = path, Content = string.Empty }).ToArray())
            : await template.RenderAsync(templateContext, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Template '{template.GetType().FullName}' returned no result.");
        var artifactRouteTable = new SiteRouteTable();
        artifactRouteTable.ReserveOutputPath(
            OutputManifestRelativePath,
            "manifest:output");
        foreach (var asset in assetRegistry.RegisteredRoutes)
        {
            artifactRouteTable.Register(asset.Route, $"asset:{asset.Asset.Id}");
        }
        var commonArtifacts = GetCommonArtifactRoutes(
            configuration,
            publishedPosts,
            contentPages,
            customization.GenerateLlmsTxt);
        var builtInTemplateOwners = builtInTemplate
            ? BuiltInSiteTemplateBuildPlanAdapter.CreateTemplateOwnerMap(
                template,
                publishedPosts,
                publishedExtraPages,
                routes)
            : null;
        var builtInCommonOwners = builtInTemplate
            ? BuiltInSiteTemplateBuildPlanAdapter.CreateCommonOwnerMap(
                publishedPosts,
                routes)
            : null;
        RegisterCommonRoutes(
            artifactRouteTable,
            commonArtifacts,
            route =>
            {
                var contentOwner = contentPages.FirstOrDefault(page =>
                    page.IsIncludedIn(GeneratedPageDerivedSurfaces.SocialImage)
                    && configuration.Routes.ContentSocialImage(page.Route).RelativeOutputPath
                        == route.RelativeOutputPath);
                if (contentOwner is not null)
                {
                    return ContentCollectionBuildPlanAdapter.SocialNodeId(contentOwner).Value;
                }

                return builtInTemplate
                    ? builtInCommonOwners![route.RelativeOutputPath].Value
                    : LegacySiteTemplateBuildPlanAdapter.CommonNodeId.Value;
            });
        var templateFiles = RegisterTemplateRoutes(
            artifactRouteTable,
            routes,
            templateResult.Files,
            commonArtifacts,
            rejectCommonArtifactConflict: !builtInTemplate,
            route => builtInTemplate
                ? builtInTemplateOwners![route.RelativeOutputPath].Value
                : LegacySiteTemplateBuildPlanAdapter.TemplateNodeId.Value);
        var normalizedCollectionFiles = RegisterCollectionRoutes(
            artifactRouteTable,
            collectionFiles,
            contentPages);
        var redirectOutputs = PrepareRedirects(redirectDeclarations, site.BaseUrl, artifactRouteTable,
            templateFiles.Select(file => file.RelativePath).Concat(normalizedCollectionFiles.Select(file => file.RelativePath))
                .Concat(commonArtifacts.Select(route => route.RelativeOutputPath)).Concat(assetRegistry.Files.Select(file => file.RelativePath)));
        var baseCommonArtifacts = commonArtifacts
            .Where(route => !contentPages.Any(page =>
                page.IsIncludedIn(GeneratedPageDerivedSurfaces.SocialImage)
                && configuration.Routes.ContentSocialImage(page.Route).RelativeOutputPath
                    == route.RelativeOutputPath))
            .ToArray();
        var basePlan = builtInTemplate
            ? BuiltInSiteTemplateBuildPlanAdapter.Create(
                template,
                site,
                customization.Text,
                customization.Theme,
                publishedPosts,
                publishedExtraPages,
                contentPages,
                routes,
                templateFiles,
                buildTimestamp,
                faviconSource,
                hasFaviconAssets,
                hasSocialImage,
                customization.GenerateLlmsTxt)
            : LegacySiteTemplateBuildPlanAdapter.Create(
                template,
                site,
                customization.Text,
                customization.Theme,
                publishedPosts,
                publishedExtraPages,
                contentPages,
                routes,
                templateFiles,
                baseCommonArtifacts,
                buildTimestamp,
                options.EnvironmentName,
                faviconSource,
                hasFaviconAssets,
                hasSocialImage,
                customization.GenerateLlmsTxt);
        var collectionNodes = ContentCollectionBuildPlanAdapter.Create(
            contentPages,
            site,
            customization.Text,
            customization.Theme,
            buildTimestamp,
            options.EnvironmentName,
            faviconSource,
            hasFaviconAssets,
            hasSocialImage,
            routes);
        var assetNodes = assetRegistry.CreateBuildNodes();
        IEnumerable<BuildNode> allNodes = assetNodes.Count == 0
            ? basePlan.Nodes.Concat(collectionNodes)
            : basePlan.Nodes.Select(node => node.Id.Equals(LegacySiteTemplateBuildPlanAdapter.TemplateNodeId)
                    ? new BuildNode(node.Id, node.Inputs, node.Dependencies.Concat(assetNodes.Select(static item => item.Id)), node.Artifacts)
                    : node)
                .Concat(collectionNodes.Select(node => node.Id.Value.StartsWith("page:collection:", StringComparison.Ordinal)
                    ? new BuildNode(node.Id, node.Inputs, node.Dependencies.Concat(assetNodes.Select(static item => item.Id)), node.Artifacts)
                    : node))
                .Concat(assetNodes);
        if (redirectOutputs.Count != 0)
        {
            var redirectPlan = SiteBuildPlan.Create(allNodes);
            allNodes = redirectPlan.Nodes.Concat(CreateRedirectNodes(redirectOutputs, site.BaseUrl, redirectPlan));
        }
        var socialImplementation = hasSocialImage ? SocialImageGenerator.GetImplementationFingerprint() : null;
        // Aggregate search reads rendered collection bodies, so execution must finish those pages first.
        var buildPlan = SiteBuildPlan.Create(allNodes.Select(node =>
        {
            IEnumerable<BuildInput> inputs = node.Inputs;
            if (node.Id.Value.StartsWith("social:", StringComparison.Ordinal))
                inputs = inputs.Append(socialImplementation is null
                    ? BuildInput.FromValue("social.cachePolicy", "always-rebuild")
                    : BuildInput.FromConfiguration("social.implementation", socialImplementation));
            if (builtInTemplate && node.Id.Value.StartsWith("page:", StringComparison.Ordinal)
                && !node.Id.Value.StartsWith("page:collection:", StringComparison.Ordinal))
                inputs = inputs.Append(BuildInput.FromConfiguration("assets.hasSocialImage", hasSocialImage.ToString(CultureInfo.InvariantCulture)));
            return new BuildNode(node.Id, inputs,
                node.Id.Value == "index:search" ? node.Dependencies.Concat(contentPages
                    .Where(page => page.IsIncludedIn(GeneratedPageDerivedSurfaces.Search)).Select(page => new BuildNodeId(page.OwnerId))) : node.Dependencies,
                node.Artifacts);
        }));
        var ownedArtifactPaths = buildPlan.Artifacts
            .Select(static artifact => artifact.RelativeOutputPath)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var currentOwnedPaths = ownedArtifactPaths
            .Append(OutputManifestRelativePath)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var outputTransaction = await OutputTransaction.CreateAsync(
                outputRoot,
                preserveExisting: !clean,
                cancellationToken)
            .ConfigureAwait(false);
        var generatedInStaging = new List<string>();
        BuildExecutionResult? execution = null;
        IReadOnlyList<string> staleRemovedArtifacts = [];
        var qualityReport = new SiteQualityReport();
        var committed = false;
        try
        {
            staleRemovedArtifacts = await outputTransaction.RemoveStaleOwnedFilesAsync(
                    currentOwnedPaths,
                    cancellationToken)
                .ConfigureAwait(false);

            if (builtInTemplate)
            {
                execution = await ExecuteBuildAsync(buildPlan, configuration, templateContext, plannedText!, contentPages,
                    assetRegistry, redirectOutputs.Select(redirect => redirect.File).ToArray(), outputTransaction,
                    buildCacheRoot, clean, options.MaxDegreeOfParallelism, cancellationToken).ConfigureAwait(false);
                generatedInStaging.AddRange(ownedArtifactPaths.Select(path => SafeCombine(outputTransaction.StagingRoot, path)));
            }
            else
            {
                foreach (var file in templateFiles)
                {
                    await WriteTextAsync(
                            outputTransaction.StagingRoot,
                            file.RelativePath,
                            file.Content,
                            generatedInStaging,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                foreach (var file in normalizedCollectionFiles)
                {
                    await WriteTextAsync(
                            outputTransaction.StagingRoot,
                            file.RelativePath,
                            file.Content,
                            generatedInStaging,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                foreach (var redirect in redirectOutputs)
                    await WriteTextAsync(outputTransaction.StagingRoot, redirect.File.RelativePath, redirect.File.Content,
                        generatedInStaging, cancellationToken).ConfigureAwait(false);

                foreach (var file in assetRegistry.Files)
                {
                    await WriteBinaryAssetAsync(
                            outputTransaction.StagingRoot,
                            file.RelativePath,
                            file.Bytes,
                            generatedInStaging,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                if (configuration.HasFaviconAssets)
                {
                    await WriteBundledFaviconAssetsAsync(
                            outputTransaction.StagingRoot,
                            configuration,
                            generatedInStaging,
                            cancellationToken)
                        .ConfigureAwait(false);
                    await WriteTextAsync(
                            outputTransaction.StagingRoot,
                            routes.WebManifest.RelativeOutputPath,
                            BuildWebManifest(configuration),
                            generatedInStaging,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                if (configuration.HasSocialImage)
                {
                    await WriteBinaryAssetAsync(
                            outputTransaction.StagingRoot,
                            routes.DefaultSocialImage.RelativeOutputPath,
                            await BuildDefaultSocialImageAsync(configuration, cancellationToken).ConfigureAwait(false),
                            generatedInStaging,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                if (configuration.HasSocialImage)
                {
                    foreach (var post in publishedPosts)
                    {
                        await WriteBinaryAssetAsync(
                                outputTransaction.StagingRoot,
                                routes.PostSocialImage(post).RelativeOutputPath,
                                await BuildPostSocialImageAsync(configuration, post, cancellationToken).ConfigureAwait(false),
                                generatedInStaging,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    foreach (var page in contentPages
                                 .Where(page => page.IsIncludedIn(
                                     GeneratedPageDerivedSurfaces.SocialImage)))
                    {
                        await WriteBinaryAssetAsync(
                                outputTransaction.StagingRoot,
                                routes.ContentSocialImage(page.Route).RelativeOutputPath,
                                await BuildContentSocialImageAsync(
                                        configuration,
                                        page,
                                        cancellationToken)
                                    .ConfigureAwait(false),
                                generatedInStaging,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                if (customization.GenerateLlmsTxt)
                {
                    await WriteTextAsync(
                            outputTransaction.StagingRoot,
                            routes.Llms.RelativeOutputPath,
                            BuildLlmsTxt(configuration, publishedPosts, contentPages),
                            generatedInStaging,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            if (options.Quality is { } qualityOptions)
            {
                var textPaths = templateFiles.Concat(normalizedCollectionFiles).Concat(redirectOutputs.Select(redirect => redirect.File))
                    .Select(file => file.RelativePath).Concat(assetRegistry.Files
                        .Where(file => file.RelativePath.EndsWith(".css", StringComparison.OrdinalIgnoreCase)).Select(file => file.RelativePath)).ToArray();
                var pageRoutes = contentPages.Select(page => page.Route).Concat(redirectOutputs.Select(redirect => redirect.Source))
                    .ToDictionary(route => route.RelativeOutputPath, StringComparer.Ordinal);
                var qualityRoutes = buildPlan.Artifacts.ToDictionary(artifact => artifact.RelativeOutputPath, artifact =>
                    pageRoutes.TryGetValue(artifact.RelativeOutputPath, out var pageRoute) ? pageRoute
                    : routes.TryGetFile(artifact.RelativeOutputPath, out var knownRoute) ? knownRoute
                    : SiteRoute.ForFile(EscapeOutputPath(artifact.RelativeOutputPath), site.BaseUrl), StringComparer.Ordinal);
                qualityReport = await SiteQualityValidator.ValidateAsync(site.BaseUrl, textPaths,
                    (path, token) => ReadStagedTextAsync(outputTransaction.StagingRoot, path, token), qualityRoutes,
                    assetRegistry.Files.Select(file => file.RelativePath).ToHashSet(StringComparer.Ordinal),
                    redirectOutputs.ToDictionary(redirect => redirect.Source.RelativeOutputPath, redirect => redirect.Target, StringComparer.Ordinal),
                    qualityOptions, cancellationToken).ConfigureAwait(false);
                if (qualityReport.Diagnostics.Any(diagnostic => diagnostic.Severity >= qualityOptions.FailureThreshold))
                    throw new SiteQualityValidationException(qualityReport);
            }

            await WriteOutputManifestAsync(
                    outputTransaction.StagingRoot,
                    ownedArtifactPaths,
                    generatedInStaging,
                    cancellationToken,
                    execution?.CacheKey)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await outputTransaction.PrepareOwnershipStateAsync(
                    generatedInStaging,
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await outputTransaction.CommitAsync(generatedInStaging).ConfigureAwait(false);
            committed = true;
        }
        finally
        {
            try
            {
                if (!committed)
                {
                    await outputTransaction.CleanupAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                await outputTransaction.DisposeAsync().ConfigureAwait(false);
            }
        }

        var generated = generatedInStaging
            .Where(path => !string.Equals(
                Path.GetRelativePath(outputTransaction.StagingRoot, path)
                    .Replace('\\', '/'),
                OutputManifestRelativePath,
                StringComparison.Ordinal))
            .Select(path => SafeCombine(
                outputRoot,
                Path.GetRelativePath(outputTransaction.StagingRoot, path)))
            .Order(StringComparer.Ordinal)
            .ToArray();
        return new SiteGenerationResult(outputRoot, publishedPosts.Length, generated)
        {
            BuildPlan = buildPlan,
            QualityReport = qualityReport,
            BuildReport = CreateBuildReport(
                buildPlan,
                options.PreviousBuildPlan,
                buildTimestamp,
                options.EnvironmentName,
                outputRoot,
                generated,
                unpublishedPages,
                staleRemovedArtifacts,
                outputTransaction.Diagnostics.Concat(qualityReport.Diagnostics).ToArray(),
                outputTransaction.RetainedRecoveryState,
                template,
                execution),
        };
    }

    private static IReadOnlyList<PageClaim<MarkdownPost>> AdaptPublishedMarkdownPages(
        IReadOnlyList<MarkdownPost> posts,
        string baseUrl,
        DateTimeOffset buildTimestamp,
        string environmentName,
        SiteRouteTable routeTable,
        List<string>? unpublishedPages = null)
    {
        var claims = new List<PageClaim<MarkdownPost>>(posts.Count);
        for (var index = 0; index < posts.Count; index++)
        {
            var post = posts[index]
                ?? throw new ArgumentException("Posts must not contain null entries.", nameof(posts));
            var ownerId = $"page:markdown:{index:D8}";
            var location = new SiteSourceLocation(post.FilePath);
            var metadata = LegacyPageAdapters.ToPageMetadata(post);
            // 公開対象外のページはルートを所有しませんが、公開メタデータ自体は常に検証します。
            if (!PagePublicationPolicy.ShouldPublish(metadata, buildTimestamp, environmentName))
            {
                unpublishedPages?.Add(LegacyPageAdapters.CreateMarkdownPageId(post).Value);
                continue;
            }

            try
            {
                claims.Add(new PageClaim<MarkdownPost>(
                    LegacyPageAdapters.ToSitePage(post, metadata, baseUrl),
                    ownerId,
                    location));
            }
            catch (Exception exception) when (exception is ArgumentException or UriFormatException)
            {
                routeTable.RegisterInvalidRoute(
                    post.RelativeOutputPath,
                    ownerId,
                    exception,
                    location);
            }
        }

        return claims;
    }

    private static SiteBuildReport CreateBuildReport(
        SiteBuildPlan plan,
        SiteBuildPlan? previousPlan,
        DateTimeOffset buildTimestamp,
        string environmentName,
        string outputRoot,
        IReadOnlyList<string> generated,
        IReadOnlyList<string> unpublishedPages,
        IReadOnlyList<string> staleRemovedArtifacts,
        IReadOnlyList<SiteDiagnostic> diagnostics,
        bool retainedRecoveryState,
        ISiteTemplate template,
        BuildExecutionResult? execution = null)
    {
        var generatedRelative = generated
            .Select(path => Path.GetRelativePath(outputRoot, path).Replace('\\', '/'))
            .Where(path => !string.Equals(path, OutputManifestRelativePath, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var invalidations = previousPlan is null
            ? []
            : plan.GetInvalidatedNodes(previousPlan)
                .Select(item => new SiteBuildReportInvalidation(
                    item.NodeId.Value,
                    item.Reasons))
                .ToArray();
        return new SiteBuildReport(
            buildTimestamp,
            environmentName,
            template.GetType().FullName ?? template.GetType().Name,
            execution?.Nodes ?? plan.Nodes.Select(node => new SiteBuildReportNode(
                node.Id.Value,
                node.Artifacts.Select(artifact => artifact.RelativeOutputPath).ToArray())),
            invalidations,
            execution is null ? generatedRelative : execution.Nodes.Where(node => !node.CacheHit).SelectMany(node => node.OwnedArtifacts),
            execution?.Nodes.Where(node => node.CacheHit).SelectMany(node => node.OwnedArtifacts) ?? [],
            unpublishedPages,
            staleRemovedArtifacts,
            diagnostics,
            retainedRecoveryState: retainedRecoveryState);
    }

    private static IReadOnlyList<PageClaim<SiteExtraPage>> AdaptExtraPages(
        IReadOnlyList<SiteExtraPage> pages,
        string baseUrl,
        SiteRouteTable routeTable)
    {
        var claims = new List<PageClaim<SiteExtraPage>>(pages.Count);
        for (var index = 0; index < pages.Count; index++)
        {
            var page = pages[index]
                ?? throw new ArgumentException("Extra pages must not contain null entries.", nameof(pages));
            var ownerId = $"page:extra:{index:D8}";
            try
            {
                claims.Add(new PageClaim<SiteExtraPage>(
                    LegacyPageAdapters.ToSitePage(page, baseUrl),
                    ownerId,
                    SourceLocation: null));
            }
            catch (Exception exception) when (exception is ArgumentException or UriFormatException)
            {
                routeTable.RegisterInvalidRoute(page.RelativePath, ownerId, exception);
            }
        }

        return claims;
    }

    private static IReadOnlyList<IntegratedContentPage> AdaptContentCollections(
        IReadOnlyList<SiteContentCollection> collections,
        string baseUrl,
        DateTimeOffset buildTimestamp,
        string environmentName,
        SiteRouteTable routeTable,
        ICollection<string> unpublishedPages,
        CancellationToken cancellationToken)
    {
        ValidateContentCollectionIds(collections);
        var pages = new List<IntegratedContentPage>();
        foreach (var collection in collections
                     .Select((value, index) => (Value: value, Index: index))
                     .OrderBy(item => item.Value?.Id.Value, StringComparer.Ordinal)
                     .ThenBy(item => item.Index))
        {
            if (collection.Value is null)
            {
                throw new ArgumentException(
                    "Content collections must not contain null entries.",
                    nameof(collections));
            }

            pages.AddRange(collection.Value.CreatePages(
                baseUrl,
                buildTimestamp,
                environmentName,
                routeTable,
                unpublishedPages,
                cancellationToken));
        }

        return pages;
    }

    private static void ValidateContentCollectionIds(
        IReadOnlyList<SiteContentCollection> collections)
    {
        var duplicateIds = collections
            .Select((collection, index) => collection
                ?? throw new ArgumentException(
                    $"Content collection at index {index} is null.",
                    nameof(collections)))
            .GroupBy(static collection => collection.Id)
            .Where(static group => group.Skip(1).Any())
            .Select(static group => group.Key.Value)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (duplicateIds.Length != 0)
        {
            throw new ArgumentException(
                $"Content collection identifiers must be unique: {string.Join(", ", duplicateIds.Select(static id => $"'{id}'"))}.",
                nameof(collections));
        }
    }

    private static IReadOnlyList<PageClaim<TContent>> FilterPublished<TContent>(
        IReadOnlyList<PageClaim<TContent>> claims,
        DateTimeOffset buildTimestamp,
        string environmentName)
        where TContent : notnull
    {
        var published = LegacyPageAdapters.FilterPublished(
            claims.Select(claim => claim.Page),
            buildTimestamp,
            environmentName);
        var publishedPages = published.ToHashSet(ReferenceEqualityComparer.Instance);
        return claims.Where(claim => publishedPages.Contains(claim.Page)).ToArray();
    }

    private static void RegisterPageRoutes<TContent>(
        SiteRouteTable routeTable,
        IReadOnlyList<PageClaim<TContent>> claims,
        bool claimsOutput)
        where TContent : notnull
    {
        foreach (var claim in claims)
        {
            if (claimsOutput)
            {
                routeTable.Register(claim.Page.Route, claim.OwnerId, claim.SourceLocation);
            }
            else
            {
                routeTable.RegisterPublicRoute(claim.Page.Route, claim.OwnerId, claim.SourceLocation);
            }
        }
    }

    private static DateTimeOffset ResolveBuildTimestamp(DateTimeOffset? explicitTimestamp)
    {
        if (explicitTimestamp is not null)
        {
            return explicitTimestamp.Value.ToUniversalTime();
        }

        var sourceDateEpoch = Environment.GetEnvironmentVariable("SOURCE_DATE_EPOCH");
        if (sourceDateEpoch is null)
        {
            return DateTimeOffset.UtcNow;
        }

        if (!long.TryParse(sourceDateEpoch, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
        {
            throw new InvalidOperationException(
                $"Environment variable SOURCE_DATE_EPOCH must be a valid Unix timestamp in whole seconds, but was '{sourceDateEpoch}'.");
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidOperationException(
                $"Environment variable SOURCE_DATE_EPOCH is outside the supported Unix timestamp range: '{sourceDateEpoch}'.",
                exception);
        }
    }

    /// <summary>Validates posts by applying the validators in <paramref name="customization"/> in order.</summary>
    /// <param name="site">Site settings.</param>
    /// <param name="contentDirectory">Content directory.</param>
    /// <param name="posts">Posts to validate.</param>
    /// <param name="customization">Overrides that include the validators. When omitted or empty, only the required-summary check runs.</param>
    public static void Validate(
        SiteSettings site,
        string contentDirectory,
        IReadOnlyList<MarkdownPost> posts,
        SiteCustomization? customization = null)
    {
        ArgumentNullException.ThrowIfNull(site);
        ArgumentNullException.ThrowIfNull(posts);
        customization ??= new SiteCustomization();
        IReadOnlyList<IContentValidator> validators = customization.Validators.Count > 0
            ? customization.Validators
            : [new RequiredSummaryValidator()];
        var context = new ContentValidationContext(site, contentDirectory);
        foreach (var post in posts)
        {
            foreach (var validator in validators)
            {
                validator.Validate(post, context);
            }
        }
    }

    internal string RenderMarkdown(string markdown) => Markdown.ToHtml(markdown, _pipeline);

    internal string GetSitePath(RenderContext configuration, string relativePath) =>
        relativePath.Length == 0
            ? configuration.Routes.PublicPath(configuration.Routes.Root)
            : configuration.Routes.PublicPath(configuration.Routes.File(relativePath));

    internal string RenderTemplateDocument(RenderContext configuration, SiteTemplateDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Layout(
            configuration,
            document.Title,
            document.BodyHtml,
            document.RelativePath,
            document.Description,
            document.OpenGraphType,
            document.PublishedAt,
            document.SocialImageRelativePath,
            includeBlogNavigation: false);
    }

    internal SiteTemplateResult RenderBlogTemplate(SiteTemplateContext templateContext)
    {
        var configuration = templateContext.Configuration;
        var posts = templateContext.Posts;
        var searchIndex = BuildSearchIndex(configuration, posts, configuration.ContentPages);
        var searchIndexFingerprint = ComputeTextContentSha256(searchIndex);
        var files = new List<SiteTemplateFile>
        {
            new() { RelativePath = configuration.Routes.SiteCss.RelativeOutputPath, Content = BuildCss(configuration) },
            new() { RelativePath = configuration.Routes.SiteScript.RelativeOutputPath, Content = BuildSiteScript() },
            new() { RelativePath = configuration.Routes.SearchScript.RelativeOutputPath, Content = BuildSearchScript(configuration.Text) },
            new() { RelativePath = configuration.Routes.SearchIndex.RelativeOutputPath, Content = searchIndex },
            new() { RelativePath = configuration.Routes.Home.RelativeOutputPath, Content = RenderIndex(configuration, posts) },
            new() { RelativePath = configuration.Routes.Archives.RelativeOutputPath, Content = RenderArchives(configuration, posts) },
            new() { RelativePath = configuration.Routes.Tags.RelativeOutputPath, Content = RenderTags(configuration, posts) }
        };

        foreach (var extraPage in configuration.ExtraPages)
        {
            files.Add(new SiteTemplateFile
            {
                RelativePath = configuration.Routes.ExtraPage(extraPage).RelativeOutputPath,
                Content = RenderExtraPage(configuration, extraPage)
            });
        }

        files.Add(new SiteTemplateFile
        {
            RelativePath = configuration.Routes.SearchPage.RelativeOutputPath,
            Content = RenderSearch(configuration, searchIndexFingerprint)
        });

        foreach (var post in posts)
        {
            files.Add(new SiteTemplateFile
            {
                RelativePath = configuration.Routes.Post(post).RelativeOutputPath,
                Content = RenderPost(configuration, post)
            });
        }

        files.Add(new SiteTemplateFile
        {
            RelativePath = configuration.Routes.Feed.RelativeOutputPath,
            Content = RenderFeed(configuration, posts, configuration.ContentPages)
        });
        files.Add(new SiteTemplateFile
        {
            RelativePath = configuration.Routes.Sitemap.RelativeOutputPath,
            Content = RenderSitemap(configuration, posts, configuration.ContentPages)
        });

        return new SiteTemplateResult(files);
    }

    internal SiteTemplateResult RenderDocsTemplate(SiteTemplateContext templateContext)
    {
        var configuration = templateContext.Configuration;
        var root = BuildDocsNavigation(templateContext.Posts);
        var orderedPosts = FlattenDocsNavigation(root).ToArray();
        var files = new List<SiteTemplateFile>
        {
            new() { RelativePath = configuration.Routes.SiteCss.RelativeOutputPath, Content = BuildDocsCss(configuration) },
            new() { RelativePath = configuration.Routes.SiteScript.RelativeOutputPath, Content = BuildDocsScript() },
            new()
            {
                RelativePath = configuration.Routes.Home.RelativeOutputPath,
                Content = RenderDocsIndex(configuration, templateContext.Navigation, orderedPosts)
            }
        };

        foreach (var post in orderedPosts)
        {
            files.Add(new SiteTemplateFile
            {
                RelativePath = configuration.Routes.Post(post).RelativeOutputPath,
                Content = RenderDocsPost(configuration, templateContext.Navigation, orderedPosts, post)
            });
        }

        foreach (var extraPage in configuration.ExtraPages)
        {
            files.Add(new SiteTemplateFile
            {
                RelativePath = configuration.Routes.ExtraPage(extraPage).RelativeOutputPath,
                Content = RenderDocsExtraPage(configuration, templateContext.Navigation, extraPage)
            });
        }

        return new SiteTemplateResult(files);
    }

    private static IReadOnlyList<SiteRoute> GetCommonArtifactRoutes(
        RenderContext configuration,
        IReadOnlyList<MarkdownPost> posts,
        IReadOnlyList<IntegratedContentPage> contentPages,
        bool generateLlmsTxt)
    {
        var artifacts = new List<SiteRoute>();
        if (configuration.HasFaviconAssets)
        {
            foreach (var asset in BundledFaviconAssets)
            {
                artifacts.Add(configuration.Routes.Favicon(asset));
            }

            artifacts.Add(configuration.Routes.WebManifest);
        }

        if (configuration.HasSocialImage)
        {
            artifacts.Add(configuration.Routes.DefaultSocialImage);
            foreach (var post in posts)
            {
                artifacts.Add(configuration.Routes.PostSocialImage(post));
            }

            foreach (var page in contentPages
                         .Where(page => page.IsIncludedIn(
                             GeneratedPageDerivedSurfaces.SocialImage)))
            {
                artifacts.Add(configuration.Routes.ContentSocialImage(page.Route));
            }
        }

        if (generateLlmsTxt)
        {
            artifacts.Add(configuration.Routes.Llms);
        }

        return artifacts;
    }

    private static void RegisterCommonRoutes(
        SiteRouteTable routeTable,
        IReadOnlyList<SiteRoute> artifacts,
        Func<SiteRoute, string> ownerId)
    {
        foreach (var artifact in artifacts)
        {
            routeTable.Register(artifact, ownerId(artifact));
        }
    }

    private static IReadOnlyList<SiteTemplateFile> RegisterTemplateRoutes(
        SiteRouteTable routeTable,
        SiteRouteCatalog routes,
        IReadOnlyList<SiteTemplateFile> files,
        IReadOnlyList<SiteRoute> commonArtifacts,
        bool rejectCommonArtifactConflict,
        Func<SiteRoute, string> ownerId)
    {
        ArgumentNullException.ThrowIfNull(files);
        var commonPaths = commonArtifacts
            .Select(route => route.RelativeOutputPath)
            .ToHashSet(StringComparer.Ordinal);
        var templatePaths = new HashSet<string>(StringComparer.Ordinal);
        var normalizedFiles = new List<SiteTemplateFile>(files.Count);
        for (var index = 0; index < files.Count; index++)
        {
            var file = files[index];
            if (file is null)
            {
                throw new InvalidOperationException("A template returned a null file.");
            }

            if (file.RelativePath is null)
            {
                throw new InvalidOperationException("A template output path must not be null.");
            }

            var fallbackOwner = $"template:file:{index:D8}";
            try
            {
                var route = routes.TryGetFile(file.RelativePath, out var knownRoute)
                    ? knownRoute
                    : routes.File(file.RelativePath);
                if (!templatePaths.Add(route.RelativeOutputPath))
                {
                    throw new InvalidOperationException(
                        $"Template output path '{file.RelativePath}' is duplicated.");
                }

                if (rejectCommonArtifactConflict
                    && commonPaths.Contains(route.RelativeOutputPath))
                {
                    throw new InvalidOperationException(
                        $"Template output path '{file.RelativePath}' conflicts with a common artifact.");
                }

                routeTable.Register(route, ownerId(route));
                normalizedFiles.Add(file with { RelativePath = route.RelativeOutputPath });
            }
            catch (Exception exception) when (exception is ArgumentException or UriFormatException)
            {
                routeTable.RegisterInvalidRoute(
                    file.RelativePath,
                    fallbackOwner,
                    exception);
            }
        }

        return normalizedFiles;
    }

    private static IReadOnlyList<SiteTemplateFile> RegisterCollectionRoutes(
        SiteRouteTable routeTable,
        IReadOnlyList<SiteTemplateFile> files,
        IReadOnlyList<IntegratedContentPage> pages)
    {
        if (files.Count != pages.Count)
        {
            throw new InvalidOperationException("Rendered collection page count does not match its route declarations.");
        }

        var normalized = new List<SiteTemplateFile>(files.Count);
        for (var index = 0; index < files.Count; index++)
        {
            var file = files[index];
            var page = pages[index];
            if (!StringComparer.Ordinal.Equals(
                    file.RelativePath,
                    page.Route.RelativeOutputPath))
            {
                throw new InvalidOperationException(
                    $"Rendered collection page '{page.PageId}' changed its declared output path.");
            }

            routeTable.Register(page.Route, page.OwnerId, page.SourceLocation);
            normalized.Add(file with { RelativePath = page.Route.RelativeOutputPath });
        }

        return normalized;
    }

    private sealed class DocsNavigationNode(string segment, string path)
    {
        public string Segment { get; } = segment;

        public string Path { get; } = path;

        public MarkdownPost? Post { get; set; }

        public Dictionary<string, DocsNavigationNode> Children { get; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private static DocsNavigationNode BuildDocsNavigation(IReadOnlyList<MarkdownPost> posts)
    {
        var root = new DocsNavigationNode(string.Empty, string.Empty);
        foreach (var post in posts)
        {
            var relativePath = post.RelativeOutputPath.Replace('\\', '/');
            var contentPath = relativePath.StartsWith("posts/", StringComparison.OrdinalIgnoreCase)
                ? relativePath["posts/".Length..]
                : relativePath;
            var withoutExtension = Path.ChangeExtension(contentPath, null)?.Replace('\\', '/') ?? contentPath;
            var segments = withoutExtension.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var node = root;
            var path = new StringBuilder();
            foreach (var segment in segments)
            {
                if (path.Length > 0)
                {
                    path.Append('/');
                }

                path.Append(segment);
                if (!node.Children.TryGetValue(segment, out var child))
                {
                    child = new DocsNavigationNode(segment, path.ToString());
                    node.Children.Add(segment, child);
                }

                node = child;
            }

            if (node.Post is not null)
            {
                if (!StringComparer.OrdinalIgnoreCase.Equals(
                        node.Post.RelativeOutputPath,
                        post.RelativeOutputPath))
                {
                    throw new InvalidOperationException(
                        $"Posts '{node.Post.FilePath}' and '{post.FilePath}' have the same documentation path.");
                }

                continue;
            }

            node.Post = post;
        }

        return root;
    }

    private IReadOnlyList<SiteTemplatePage> BuildTemplatePages(
        RenderContext configuration,
        DocsNavigationNode root,
        bool renderContent = true)
    {
        var orderedPosts = FlattenDocsNavigation(root).ToArray();
        var pages = orderedPosts
            .Select(post =>
            {
                var contentHtml = !renderContent ? string.Empty : AddNewTabAttributesToExternalPostLinks(
                    configuration,
                    NormalizePostBodyHeadings(RenderMarkdown(post.MarkdownBody)));
                return new SiteTemplatePage
                {
                    Post = post,
                    Url = configuration.Routes.PublicPath(configuration.Routes.Post(post)),
                    ContentHtml = contentHtml,
                    Headings = ExtractTemplateHeadings(contentHtml)
                };
            })
            .ToArray();

        return pages
            .Select((page, index) => page with
            {
                Previous = index > 0
                    ? new SiteTemplatePageLink(GetDocsLabel(pages[index - 1].Post), pages[index - 1].Url)
                    : null,
                Next = index + 1 < pages.Length
                    ? new SiteTemplatePageLink(GetDocsLabel(pages[index + 1].Post), pages[index + 1].Url)
                    : null
            })
            .ToArray();
    }

    private static SiteTemplateNavigationNode BuildTemplateNavigation(
        RenderContext configuration,
        DocsNavigationNode root,
        IReadOnlyList<SiteTemplatePage> pages)
    {
        var pagesByPost = pages.ToDictionary(page => page.Post);
        return BuildTemplateNavigationNode(root, pagesByPost, configuration.Site.Title);
    }

    private static SiteTemplateNavigationNode BuildTemplateNavigationNode(
        DocsNavigationNode node,
        IReadOnlyDictionary<MarkdownPost, SiteTemplatePage> pagesByPost,
        string rootLabel)
    {
        return new SiteTemplateNavigationNode
        {
            Label = string.IsNullOrEmpty(node.Segment)
                ? rootLabel
                : GetDocsNavigationLabel(node),
            Page = node.Post is not null ? pagesByPost[node.Post] : null,
            Children = OrderDocsNavigationChildren(node)
                .Select(child => BuildTemplateNavigationNode(child, pagesByPost, rootLabel))
                .ToArray()
        };
    }

    private static IEnumerable<MarkdownPost> FlattenDocsNavigation(DocsNavigationNode node)
    {
        foreach (var child in OrderDocsNavigationChildren(node))
        {
            if (child.Post is not null)
            {
                yield return child.Post;
            }

            foreach (var post in FlattenDocsNavigation(child))
            {
                yield return post;
            }
        }
    }

    private static IEnumerable<DocsNavigationNode> OrderDocsNavigationChildren(DocsNavigationNode node) =>
        node.Children.Values
            .OrderBy(GetDocsNavigationPosition)
            .ThenBy(GetDocsNavigationLabel, StringComparer.OrdinalIgnoreCase)
            .ThenBy(child => child.Path, StringComparer.Ordinal);

    private static int GetDocsNavigationPosition(DocsNavigationNode node)
    {
        var ownPosition = node.Post?.FrontMatter.SidebarPosition;
        var childPosition = node.Children.Values
            .Select(GetDocsNavigationPosition)
            .DefaultIfEmpty(int.MaxValue)
            .Min();
        return ownPosition ?? childPosition;
    }

    private static string GetDocsNavigationLabel(DocsNavigationNode node)
    {
        if (node.Post is not null)
        {
            return GetDocsLabel(node.Post);
        }

        return FormatDocsFolderLabel(node.Segment);
    }

    private static string GetDocsLabel(MarkdownPost post) =>
        string.IsNullOrWhiteSpace(post.FrontMatter.SidebarLabel)
            ? post.FrontMatter.Title
            : post.FrontMatter.SidebarLabel.Trim();

    private static string FormatDocsFolderLabel(string value) =>
        value.Replace('-', ' ').Replace('_', ' ');

    private static string RenderDocsIndex(
        RenderContext configuration,
        SiteTemplateNavigationNode root,
        IReadOnlyList<MarkdownPost> orderedPosts)
    {
        var body = new StringBuilder();
        body.AppendLine("<section class=\"docs-hero\">");
        body.AppendLine($"<p class=\"docs-kicker\">Documentation</p>");
        body.AppendLine($"<h1>{Html.Encode(configuration.Site.Title)}</h1>");
        body.AppendLine($"<p>{Html.Encode(configuration.Site.Description)}</p>");
        if (orderedPosts.Count > 0)
        {
            var first = orderedPosts[0];
            body.AppendLine($"<p><a class=\"docs-primary-link\" href=\"{Html.Encode(configuration.Routes.PublicPath(configuration.Routes.Post(first)))}\">Start reading</a></p>");
        }

        body.AppendLine("</section>");
        return DocsLayout(configuration, root, configuration.Site.Title, body.ToString(), "index.html", "index.html", null);
    }

    private string RenderDocsPost(
        RenderContext configuration,
        SiteTemplateNavigationNode root,
        IReadOnlyList<MarkdownPost> orderedPosts,
        MarkdownPost post)
    {
        var postBody = AddNewTabAttributesToExternalPostLinks(
            configuration,
            NormalizePostBodyHeadings(RenderMarkdown(post.MarkdownBody)));
        var currentIndex = Array.IndexOf(orderedPosts.ToArray(), post);
        var body = new StringBuilder();
        body.AppendLine($"<h1>{Html.Encode(post.FrontMatter.Title)}</h1>");
        if (!string.IsNullOrWhiteSpace(post.FrontMatter.Summary))
        {
            body.AppendLine($"<p class=\"docs-description\">{Html.Encode(post.FrontMatter.Summary)}</p>");
        }

        body.AppendLine(postBody);
        body.AppendLine(RenderDocsPagination(configuration, orderedPosts, currentIndex));
        return DocsLayout(
            configuration,
            root,
            post.FrontMatter.Title,
            body.ToString(),
            post.RelativeOutputPath,
            post.RelativeOutputPath,
            RenderDocsTableOfContents(configuration, postBody),
            post.FrontMatter.Summary,
            "article",
            post.FrontMatter.Date,
            configuration.HasSocialImage ? configuration.Routes.PostSocialImage(post).RelativeOutputPath : null);
    }

    private static string RenderDocsExtraPage(
        RenderContext configuration,
        SiteTemplateNavigationNode root,
        SiteExtraPage page) =>
        DocsLayout(configuration, root, page.Title, page.BodyHtml, page.RelativePath, page.RelativePath, null);

    private static string RenderDocsTableOfContents(RenderContext configuration, string postBody) =>
        RenderTableOfContents(configuration, postBody)
            .Replace("class=\"post-toc\"", "class=\"post-toc docs-toc\"", StringComparison.Ordinal);

    private static string RenderDocsSidebar(
        RenderContext configuration,
        SiteTemplateNavigationNode root,
        string? currentPagePath)
    {
        var links = configuration.ExtraPages
            .Where(page => !string.IsNullOrWhiteSpace(page.NavLabel))
            .Select(page => (page.NavLabel!, configuration.Routes.PublicPath(configuration.Routes.ExtraPage(page)), false))
            .Concat(configuration.ContentPages
                .Where(page => page.IsIncludedIn(GeneratedPageDerivedSurfaces.Navigation)
                    && !string.IsNullOrWhiteSpace(page.Metadata.Title))
                .Select(page => (page.Metadata.Title!, configuration.Routes.PublicPath(page.Route), false)));
        var currentUrl = string.IsNullOrWhiteSpace(currentPagePath)
            ? null
            : configuration.Routes.PublicPath(configuration.Routes.File(currentPagePath));
        return DocsNavigationComponent.RenderCore(root, links, currentUrl);
    }
    private static string RenderExtraPage(RenderContext configuration, SiteExtraPage page) =>
        Layout(configuration, page.Title, page.BodyHtml, page.RelativePath);

    private static string BuildLlmsTxt(
        RenderContext configuration,
        IReadOnlyList<MarkdownPost> posts,
        IReadOnlyList<IntegratedContentPage> contentPages)
    {
        var site = configuration.Site;
        var builder = new StringBuilder();
        builder.Append("# ").Append(site.Title).Append('\n');
        if (!string.IsNullOrWhiteSpace(site.Description))
        {
            builder.Append("\n> ").Append(site.Description).Append('\n');
        }

        builder.Append("\n## ").Append(configuration.Text.LlmsPostsHeading).Append('\n');
        var includedContentPages = contentPages
            .Where(page => page.IsIncludedIn(GeneratedPageDerivedSurfaces.LlmsTxt))
            .ToArray();
        if (posts.Count == 0 && includedContentPages.Length == 0)
        {
            return builder.ToString();
        }

        builder.Append('\n');
        foreach (var post in posts)
        {
            builder.Append("- [").Append(post.FrontMatter.Title).Append("](")
                .Append(configuration.Routes.AbsoluteUrl(configuration.Routes.Post(post))).Append(')');
            if (!string.IsNullOrWhiteSpace(post.FrontMatter.Summary))
            {
                builder.Append(": ").Append(post.FrontMatter.Summary);
            }

            builder.Append('\n');
        }

        foreach (var page in includedContentPages)
        {
            builder.Append("- [")
                .Append(page.Metadata.Title ?? page.EntryId.Value)
                .Append("](")
                .Append(configuration.Routes.AbsoluteUrl(page.Route))
                .Append(')');
            if (!string.IsNullOrWhiteSpace(page.Metadata.Description))
            {
                builder.Append(": ").Append(page.Metadata.Description);
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }

    private string RenderIndex(RenderContext configuration, IReadOnlyList<MarkdownPost> posts)
    {
        var body = new StringBuilder();
        body.AppendLine("<section class=\"hero\">");
        body.AppendLine($"<p class=\"eyebrow\">{Html.Encode(configuration.Site.Description)}</p>");
        body.AppendLine($"<h1>{Html.Encode(configuration.Site.Title)}</h1>");
        body.AppendLine($"<p>{configuration.Text.IndexLede}</p>");
        body.AppendLine("</section>");
        var latestPosts = posts.Take(1).ToArray();
        var olderPosts = posts.Skip(latestPosts.Length).ToArray();
        body.AppendLine("<section>");
        body.AppendLine("<h2>Latest post</h2>");
        if (posts.Count == 0)
        {
            body.AppendLine($"<p>{configuration.Text.IndexEmpty}</p>");
        }
        else
        {
            body.AppendLine("<div class=\"featured-post-list\">");
            foreach (var post in latestPosts)
            {
                body.AppendLine(RenderPostCard(configuration, post, "featured-card"));
            }

            body.AppendLine("</div>");
        }

        body.AppendLine("</section>");
        body.AppendLine("<section>");
        body.AppendLine($"<h2>{configuration.Text.OlderPostsHeading}</h2>");
        if (olderPosts.Length == 0)
        {
            body.AppendLine($"<p>{configuration.Text.OlderPostsEmpty}</p>");
        }
        else
        {
            body.AppendLine(RenderMonthlyPostPanels(configuration, olderPosts));
        }

        body.AppendLine("</section>");
        return Layout(configuration, configuration.Site.Title, body.ToString(), "index.html");
    }

    private string RenderArchives(RenderContext configuration, IReadOnlyList<MarkdownPost> posts)
    {
        var body = new StringBuilder();
        body.AppendLine("<h1>Archives</h1>");
        foreach (var group in posts.GroupBy(post => SiteFormatting.FormatMonth(configuration.Site, post.FrontMatter.Date)))
        {
            body.AppendLine($"<h2>{Html.Encode(group.Key)}</h2>");
            body.AppendLine("<div class=\"archive-post-list\">");
            foreach (var post in group)
            {
                body.AppendLine(RenderArchivePostRow(configuration, post));
            }

            body.AppendLine("</div>");
        }

        if (posts.Count == 0)
        {
            body.AppendLine($"<p>{configuration.Text.ArchivesEmpty}</p>");
        }

        return Layout(configuration, "Archives", body.ToString(), "archives.html");
    }

    private string RenderTags(RenderContext configuration, IReadOnlyList<MarkdownPost> posts)
    {
        var tags = posts
            .SelectMany(post => post.FrontMatter.Tags.Select(tag => new { Tag = tag, Post = post }))
            .GroupBy(item => item.Tag, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var body = new StringBuilder();
        body.AppendLine("<section class=\"hero\">");
        body.AppendLine("<p class=\"eyebrow\">Tags</p>");
        body.AppendLine($"<h1>{configuration.Text.TagsHeading}</h1>");
        body.AppendLine($"<p>{configuration.Text.TagsIntro}</p>");
        body.AppendLine("</section>");
        body.AppendLine("<section>");
        body.AppendLine($"<h2>{configuration.Text.ExistingTagsHeading}</h2>");
        if (tags.Length == 0)
        {
            body.AppendLine($"<p>{configuration.Text.TagsEmpty}</p>");
        }
        else
        {
            body.AppendLine("<div class=\"tag-cloud\">");
            foreach (var group in tags)
            {
                var tag = group.Key;
                var count = group.Select(item => item.Post).Distinct().Count();
                var href = $"{configuration.Routes.PublicPath(configuration.Routes.SearchPage)}?tag={Uri.EscapeDataString(tag)}";
                body.AppendLine($"""
                    <a class="tag-link" href="{Html.Encode(href)}">
                      <span>{Html.Encode(tag)}</span>
                      <small>{count.ToString(CultureInfo.InvariantCulture)} post(s)</small>
                    </a>
                    """);
            }

            body.AppendLine("</div>");
        }
        body.AppendLine("</section>");

        return Layout(configuration, "Tags", body.ToString(), "tags.html");
    }


    private string RenderPost(RenderContext configuration, MarkdownPost post)
    {
        var postBody = AddNewTabAttributesToExternalPostLinks(
            configuration,
            NormalizePostBodyHeadings(Markdown.ToHtml(post.MarkdownBody, _pipeline)));
        var toc = RenderTableOfContents(configuration, postBody);
        var body = new StringBuilder();
        body.AppendLine("<div class=\"post-layout\">");
        body.AppendLine("<article class=\"post\">");
        body.AppendLine($"<p class=\"eyebrow\">{Html.Encode(SiteFormatting.FormatDateTime(configuration.Site, post.FrontMatter.Date))}</p>");
        body.AppendLine($"<h1 class=\"post-title\">{Html.Encode(post.FrontMatter.Title)}</h1>");
        if (!string.IsNullOrWhiteSpace(post.FrontMatter.Summary))
        {
            body.AppendLine($"<p class=\"summary\">{Html.Encode(post.FrontMatter.Summary)}</p>");
        }

        if (post.FrontMatter.Tags.Count > 0)
        {
            body.AppendLine("<div class=\"tags\">");
            foreach (var tag in post.FrontMatter.Tags)
            {
                body.AppendLine($"<span>{Html.Encode(tag)}</span>");
            }

            body.AppendLine("</div>");
        }

        body.AppendLine(postBody);
        body.AppendLine("</article>");
        body.AppendLine(toc);
        body.AppendLine("</div>");
        return Layout(
            configuration,
            post.FrontMatter.Title,
            body.ToString(),
            post.RelativeOutputPath,
            post.FrontMatter.Summary,
            "article",
            post.FrontMatter.Date,
            configuration.HasSocialImage ? configuration.Routes.PostSocialImage(post).RelativeOutputPath : null);
    }

    private static string RenderTableOfContents(RenderContext configuration, string postBody)
    {
        return RenderTemplateTableOfContents(
            configuration,
            ExtractTemplateHeadings(postBody));
    }

    private static IReadOnlyList<SiteTemplateHeading> ExtractTemplateHeadings(string postBody) =>
        HeadingRegex.Matches(postBody)
            .Select(match => new
            {
                Level = int.Parse(match.Groups["level"].Value, CultureInfo.InvariantCulture),
                Id = match.Groups["id"].Value,
                Text = StripTagsRegex.Replace(match.Groups["text"].Value, string.Empty)
            })
            .Where(heading => !string.IsNullOrWhiteSpace(heading.Id) && !string.IsNullOrWhiteSpace(heading.Text))
            .Select(heading => new SiteTemplateHeading(heading.Level, heading.Id, heading.Text))
            .ToArray();

    private static string NormalizePostBodyHeadings(string postBody)
    {
        var withoutNestedTitle = BodyHeadingOneOpenRegex.Replace(postBody, "<h2${attributes}>");
        return BodyHeadingOneCloseRegex.Replace(withoutNestedTitle, "</h2>");
    }

    private static string AddNewTabAttributesToExternalPostLinks(RenderContext configuration, string postBody)
    {
        var siteUri = new Uri(configuration.Routes.AbsoluteRootUrl, UriKind.Absolute);

        return AnchorTagRegex.Replace(postBody, match =>
        {
            var href = WebUtility.HtmlDecode(match.Groups["href"].Value);
            if (!ShouldOpenInNewTab(siteUri, href))
            {
                return match.Value;
            }

            var targetAttribute = HasAnchorAttribute(match.Value, "target") ? string.Empty : " target=\"_blank\"";
            var relAttribute = HasAnchorAttribute(match.Value, "rel") ? string.Empty : " rel=\"noopener noreferrer\"";
            return $"<a{match.Groups["before"].Value} href=\"{match.Groups["href"].Value}\"{match.Groups["after"].Value}{targetAttribute}{relAttribute}>";
        });
    }

    private static bool ShouldOpenInNewTab(Uri siteUri, string href)
    {
        if (string.IsNullOrWhiteSpace(href) || href.StartsWith('#'))
        {
            return false;
        }

        if (!Uri.TryCreate(href, UriKind.Absolute, out var linkUri))
        {
            return false;
        }

        if (!linkUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !linkUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !Uri.Compare(siteUri, linkUri, UriComponents.SchemeAndServer, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase).Equals(0);
    }

    private static bool HasAnchorAttribute(string anchorTag, string attributeName) =>
        anchorTag.Contains($"{attributeName}=", StringComparison.OrdinalIgnoreCase);

    private static string RenderPostCard(RenderContext configuration, MarkdownPost post, string? extraClass = null)
    {
        var href = configuration.Routes.PublicPath(configuration.Routes.Post(post));
        var displayDate = SiteFormatting.FormatDateTime(configuration.Site, post.FrontMatter.Date);
        var cardClass = string.IsNullOrWhiteSpace(extraClass) ? "card" : $"card {extraClass}";
        return $"""
            <article class="{cardClass}">
              <time datetime="{post.FrontMatter.Date:O}">{Html.Encode(displayDate)}</time>
              <h3><a href="{Html.Encode(href)}">{Html.Encode(post.FrontMatter.Title)}</a></h3>
              <p>{Html.Encode(post.FrontMatter.Summary)}</p>
            </article>
            """;
    }

    private static string RenderArchivePostRow(RenderContext configuration, MarkdownPost post)
    {
        var href = configuration.Routes.PublicPath(configuration.Routes.Post(post));
        var displayDate = SiteFormatting.FormatDateTime(configuration.Site, post.FrontMatter.Date);
        return $"""
            <article class="archive-row">
              <time datetime="{post.FrontMatter.Date:O}">{Html.Encode(displayDate)}</time>
              <h3><a href="{Html.Encode(href)}">{Html.Encode(post.FrontMatter.Title)}</a></h3>
              <p>{Html.Encode(post.FrontMatter.Summary)}</p>
            </article>
            """;
    }

    private static string RenderMonthlyPostPanels(RenderContext configuration, IReadOnlyList<MarkdownPost> posts)
    {
        var body = new StringBuilder();
        body.AppendLine("<div class=\"archive-panels\">");
        foreach (var group in posts.GroupBy(post => SiteFormatting.FormatMonth(configuration.Site, post.FrontMatter.Date)))
        {
            var countText = group.Count().ToString(CultureInfo.InvariantCulture);
            body.AppendLine("<details class=\"archive-panel\">");
            body.AppendLine($"<summary><span>{Html.Encode(group.Key)}</span><small>{countText} post(s)</small></summary>");
            body.AppendLine("<div class=\"archive-post-list\">");
            foreach (var post in group)
            {
                body.AppendLine(RenderArchivePostRow(configuration, post));
            }

            body.AppendLine("</div>");
            body.AppendLine("</details>");
        }

        body.AppendLine("</div>");
        return body.ToString();
    }

    private string RenderSearch(RenderContext configuration, string searchIndexFingerprint)
    {
        var indexPath =
            $"{configuration.Routes.PublicPath(configuration.Routes.SearchIndex)}?v={searchIndexFingerprint}";
        var scriptPath = configuration.Routes.PublicPath(configuration.Routes.SearchScript);
        var body = new StringBuilder();
        body.AppendLine("<section class=\"hero search-hero\">");
        body.AppendLine("<p class=\"eyebrow\">Search</p>");
        body.AppendLine($"<h1>{configuration.Text.SearchHeading}</h1>");
        body.AppendLine($"<p>{configuration.Text.SearchIntro}</p>");
        body.AppendLine($"<form class=\"search-box\" role=\"search\" action=\"{Html.Encode(configuration.Routes.PublicPath(configuration.Routes.SearchPage))}\" method=\"get\">");
        body.AppendLine($"<input id=\"search-input\" name=\"q\" type=\"search\" autocomplete=\"off\" autocapitalize=\"off\" spellcheck=\"false\" enterkeyhint=\"search\" placeholder=\"{configuration.Text.SearchInputPlaceholder}\" aria-label=\"{configuration.Text.SearchInputLabel}\" autofocus>");
        body.AppendLine("</form>");
        body.AppendLine("<p id=\"search-scope\" class=\"search-scope\" role=\"status\" aria-live=\"polite\" hidden></p>");
        body.AppendLine($"<p id=\"search-status\" class=\"search-status\" role=\"status\" aria-live=\"polite\">{configuration.Text.SearchLoading}</p>");
        body.AppendLine("</section>");
        body.AppendLine($"<section id=\"search-app\" data-index=\"{Html.Encode(indexPath)}\">");
        body.AppendLine("<div id=\"search-results\" class=\"archive-post-list\"></div>");
        body.AppendLine("<noscript><p>" + configuration.Text.SearchNoscriptPrefix + "<a href=\"" + Html.Encode(configuration.Routes.PublicPath(configuration.Routes.Archives)) + "\">" + configuration.Text.SearchNoscriptArchivesLinkText + "</a>" + configuration.Text.SearchNoscriptSuffix + "</p></noscript>");
        body.AppendLine("</section>");
        body.AppendLine($"<script src=\"{Html.Encode(scriptPath)}\" defer></script>");
        return Layout(configuration, "Search", body.ToString(), "search.html");
    }


    private string BuildSearchIndex(
        RenderContext configuration,
        IReadOnlyList<MarkdownPost> posts,
        IReadOnlyList<IntegratedContentPage> contentPages)
    {
        var documents = new List<SearchDocument>(posts.Count);
        foreach (var post in posts)
        {
            var plain = Markdown.ToPlainText(post.MarkdownBody, _pipeline);
            documents.Add(new SearchDocument(
                post.FrontMatter.Title,
                post.FrontMatter.Summary,
                post.FrontMatter.Tags,
                configuration.Routes.PublicPath(configuration.Routes.Post(post)),
                SiteFormatting.FormatDateTime(configuration.Site, post.FrontMatter.Date),
                NormalizeForIndex(plain)));
        }

        foreach (var page in contentPages
                     .Where(page => page.IsIncludedIn(GeneratedPageDerivedSurfaces.Search)))
        {
            documents.Add(new SearchDocument(
                page.Metadata.Title ?? page.EntryId.Value,
                page.Metadata.Description ?? string.Empty,
                [],
                configuration.Routes.PublicPath(page.Route),
                page.Metadata.PublishFrom is { } published
                    ? SiteFormatting.FormatDateTime(configuration.Site, published)
                    : string.Empty,
                NormalizeForIndex(StripTagsRegex.Replace(page.DerivedContent ?? string.Empty, " "))));
        }

        var index = new SearchIndex(
            configuration.Site.Title,
            configuration.BuildTimestamp.ToString("O", CultureInfo.InvariantCulture),
            documents);
        return JsonSerializer.Serialize(index, SearchSerializerContext.SearchIndex);
    }


    private static string NormalizeForIndex(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var previousWhitespace = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!previousWhitespace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                previousWhitespace = true;
                continue;
            }

            builder.Append(ch);
            previousWhitespace = false;
        }

        var normalized = builder.ToString().TrimEnd();
        return normalized.Length > SearchIndexMaxLength ? normalized[..SearchIndexMaxLength] : normalized;
    }

    private string RenderFeed(
        RenderContext configuration,
        IReadOnlyList<MarkdownPost> posts,
        IReadOnlyList<IntegratedContentPage> contentPages)
    {
        var settings = new XmlWriterSettings { Indent = true, Encoding = Encoding.UTF8 };
        using var stringWriter = new Utf8StringWriter();
        using var writer = XmlWriter.Create(stringWriter, settings);
        writer.WriteStartDocument();
        writer.WriteStartElement("rss");
        writer.WriteAttributeString("version", "2.0");
        writer.WriteStartElement("channel");
        writer.WriteElementString("title", configuration.Site.Title);
        writer.WriteElementString("description", configuration.Site.Description);
        writer.WriteElementString("link", configuration.Routes.AbsoluteRootUrl);
        foreach (var post in posts.Take(20))
        {
            var url = configuration.Routes.AbsoluteUrl(configuration.Routes.Post(post));
            writer.WriteStartElement("item");
            writer.WriteElementString("title", post.FrontMatter.Title);
            writer.WriteElementString("description", post.FrontMatter.Summary);
            writer.WriteElementString("link", url);
            writer.WriteElementString("guid", url);
            writer.WriteElementString("pubDate", post.FrontMatter.Date.UtcDateTime.ToString("R", CultureInfo.InvariantCulture));
            writer.WriteEndElement();
        }

        foreach (var page in contentPages
                     .Where(page => page.IsIncludedIn(GeneratedPageDerivedSurfaces.Rss))
                     .Take(Math.Max(0, 20 - posts.Count)))
        {
            var url = configuration.Routes.AbsoluteUrl(page.Route);
            writer.WriteStartElement("item");
            writer.WriteElementString("title", page.Metadata.Title ?? page.EntryId.Value);
            writer.WriteElementString("description", page.Metadata.Description ?? string.Empty);
            writer.WriteElementString("link", url);
            writer.WriteElementString("guid", url);
            writer.WriteElementString(
                "pubDate",
                (page.Metadata.PublishFrom ?? configuration.BuildTimestamp)
                    .UtcDateTime
                    .ToString("R", CultureInfo.InvariantCulture));
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
        writer.Flush();
        return stringWriter.ToString();
    }

    private string RenderSitemap(
        RenderContext configuration,
        IReadOnlyList<MarkdownPost> posts,
        IReadOnlyList<IntegratedContentPage> contentPages)
    {
        var settings = new XmlWriterSettings { Indent = true, Encoding = Encoding.UTF8 };
        using var stringWriter = new Utf8StringWriter();
        using var writer = XmlWriter.Create(stringWriter, settings);
        writer.WriteStartDocument();
        writer.WriteStartElement("urlset", "http://www.sitemaps.org/schemas/sitemap/0.9");
        WriteSitemapUrl(writer, configuration.Routes.AbsoluteUrl(configuration.Routes.Home));
        WriteSitemapUrl(writer, configuration.Routes.AbsoluteUrl(configuration.Routes.Archives));
        WriteSitemapUrl(writer, configuration.Routes.AbsoluteUrl(configuration.Routes.Tags));
        foreach (var extraPage in configuration.ExtraPages)
        {
            if (extraPage.IncludeInSitemap)
            {
                WriteSitemapUrl(
                    writer,
                    configuration.Routes.AbsoluteUrl(configuration.Routes.ExtraPage(extraPage)));
            }
        }

        WriteSitemapUrl(writer, configuration.Routes.AbsoluteUrl(configuration.Routes.SearchPage));
        foreach (var post in posts)
        {
            WriteSitemapUrl(
                writer,
                configuration.Routes.AbsoluteUrl(configuration.Routes.Post(post)),
                post.FrontMatter.Date);
        }

        foreach (var page in contentPages
                     .Where(page => page.IsIncludedIn(GeneratedPageDerivedSurfaces.Sitemap)))
        {
            WriteSitemapUrl(
                writer,
                configuration.Routes.AbsoluteUrl(page.Route),
                page.Metadata.PublishFrom);
        }

        writer.WriteEndElement();
        writer.WriteEndDocument();
        writer.Flush();
        return stringWriter.ToString();
    }

    private static void WriteSitemapUrl(XmlWriter writer, string loc, DateTimeOffset? lastModified = null)
    {
        writer.WriteStartElement("url");
        writer.WriteElementString("loc", loc);
        if (lastModified is not null)
        {
            writer.WriteElementString("lastmod", lastModified.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        writer.WriteEndElement();
    }

    private static async Task WriteTextAsync(string outputRoot, string relativePath, string contents, List<string> generated, CancellationToken cancellationToken)
    {
        var fullPath = SafeCombine(outputRoot, relativePath);
        CreateSafeDirectory(outputRoot, Path.GetDirectoryName(fullPath)!);
        await WriteNewFileAsync(
                fullPath,
                GetTextContentBytes(contents),
                cancellationToken)
            .ConfigureAwait(false);
        generated.Add(fullPath);
    }

    internal static string ComputeTextContentSha256(string contents) =>
        Convert.ToHexStringLower(SHA256.HashData(GetTextContentBytes(contents)));

    private static byte[] GetTextContentBytes(string contents) =>
        Encoding.UTF8.GetBytes(contents.ReplaceLineEndings("\n"));

    private static bool ContainsDirectory(string root, string path)
    {
        root = Path.TrimEndingDirectorySeparator(root);
        return string.Equals(root, Path.TrimEndingDirectorySeparator(path), PathComparison)
            || path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, PathComparison);
    }

    private static async Task WriteBinaryAssetAsync(string outputRoot, string relativePath, byte[] contents, List<string> generated, CancellationToken cancellationToken)
    {
        var fullPath = SafeCombine(outputRoot, relativePath);
        CreateSafeDirectory(outputRoot, Path.GetDirectoryName(fullPath)!);
        if (File.Exists(fullPath))
        {
            await using var existing = BuildInputFingerprint.OpenVerifiedContainedRead(outputRoot, fullPath, asynchronous: true);
            if (existing.Length == contents.Length
                && (await SHA256.HashDataAsync(existing, cancellationToken).ConfigureAwait(false))
                    .AsSpan().SequenceEqual(SHA256.HashData(contents)))
            {
                generated.Add(fullPath);
                return;
            }
        }
        await WriteNewFileAsync(fullPath, contents, cancellationToken).ConfigureAwait(false);
        generated.Add(fullPath);
    }

    private static async Task WriteOutputManifestAsync(
        string outputRoot,
        IReadOnlyList<string> ownedArtifactPaths,
        List<string> generated,
        CancellationToken cancellationToken,
        string? buildCacheKey = null)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   stream,
                   new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", OutputManifestVersion);
            if (buildCacheKey is not null) writer.WriteString("buildCache", buildCacheKey);
            writer.WritePropertyName("files");
            writer.WriteStartArray();
            foreach (var path in ownedArtifactPaths)
            {
                writer.WriteStringValue(path);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        var bytes = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(stream.ToArray()).ReplaceLineEndings("\n") + "\n");
        await WriteBinaryAssetAsync(
                outputRoot,
                OutputManifestRelativePath,
                bytes,
                generated,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<GeneratedArtifactState>> CreateArtifactStatesAsync(
        string outputRoot,
        IReadOnlyCollection<string> generatedFiles,
        CancellationToken cancellationToken)
    {
        var artifacts = new List<GeneratedArtifactState>(generatedFiles.Count);
        foreach (var generatedFile in generatedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(outputRoot, generatedFile)
                .Replace('\\', '/');
            ValidateOwnedArtifactPath(relativePath, "generated output");
            artifacts.Add(new GeneratedArtifactState(
                relativePath,
                await ComputeFileSha256Async(generatedFile, cancellationToken)
                    .ConfigureAwait(false)));
        }

        EnsureNoArtifactPathAliases(artifacts, "generated output");
        return artifacts
            .OrderBy(static artifact => artifact.Path, StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task WriteOwnershipStateAsync(
        string path,
        OutputOwnershipState state,
        CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   memory,
                   new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", OutputOwnershipStateVersion);
            writer.WriteString("outputIdentity", state.OutputIdentity);
            writer.WritePropertyName("artifacts");
            writer.WriteStartArray();
            foreach (var artifact in state.Artifacts)
            {
                writer.WriteStartObject();
                writer.WriteString("path", artifact.Path);
                writer.WriteString("sha256", artifact.Sha256);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        var bytes = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(memory.ToArray()).ReplaceLineEndings("\n") + "\n");
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 4096,
            Options = FileOptions.Asynchronous | FileOptions.WriteThrough
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        await using var stream = new FileStream(path, options);
        EnsureOpenedFilePath(stream, path);
        OutputTransaction.RestrictTransactionPathToOwner(path, isDirectory: false);
        OutputTransaction.EnsureTransactionPathIsOwnerRestricted(
            path,
            isDirectory: false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static async Task<OutputOwnershipState?> ReadOwnershipStateAsync(
        string path,
        string expectedOutputIdentity,
        CancellationToken cancellationToken)
    {
        var attributes = GetAttributes(path);
        if (attributes is null)
        {
            return null;
        }

        EnsureNotNameSurrogateReparsePoint(path, attributes.Value);
        if ((attributes.Value & FileAttributes.Directory) != 0)
        {
            throw new InvalidOperationException(
                $"Output ownership state '{path}' is not a regular file.");
        }

        OutputTransaction.EnsureTransactionPathIsOwnerRestricted(
            path,
            isDirectory: false);
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            EnsureOpenedFilePath(stream, path);
            var bytes = new byte[stream.Length];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.EnumerateObject().Count() != 3
                || !root.TryGetProperty("version", out var version)
                || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var versionValue)
                || versionValue != OutputOwnershipStateVersion
                || !root.TryGetProperty("outputIdentity", out var outputIdentity)
                || outputIdentity.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("artifacts", out var artifactsElement)
                || artifactsElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException(
                    $"Output ownership state '{path}' has an unsupported format.");
            }

            var actualOutputIdentity = outputIdentity.GetString()!;
            if (!IsSha256(actualOutputIdentity)
                || !string.Equals(
                    actualOutputIdentity,
                    expectedOutputIdentity,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Output ownership state '{path}' does not match its canonical output identity.");
            }

            var artifacts = new List<GeneratedArtifactState>();
            foreach (var item in artifactsElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || item.EnumerateObject().Count() != 2
                    || !item.TryGetProperty("path", out var artifactPath)
                    || artifactPath.ValueKind != JsonValueKind.String
                    || !item.TryGetProperty("sha256", out var sha256)
                    || sha256.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidOperationException(
                        $"Output ownership state '{path}' contains an invalid artifact.");
                }

                var relativePath = artifactPath.GetString()!;
                ValidateOwnedArtifactPath(relativePath, path);
                var fingerprint = sha256.GetString()!;
                if (!IsSha256(fingerprint))
                {
                    throw new InvalidOperationException(
                        $"Output ownership state '{path}' contains an invalid fingerprint.");
                }

                artifacts.Add(new GeneratedArtifactState(relativePath, fingerprint));
            }

            EnsureNoArtifactPathAliases(artifacts, path);
            return new OutputOwnershipState(actualOutputIdentity, artifacts);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"Output ownership state '{path}' is invalid.",
                exception);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                $"Output ownership state '{path}' contains an unsafe path.",
                exception);
        }
    }

    private static async Task<string> ComputeFileSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        EnsureOpenedFilePath(stream, path);
        var attributes = File.GetAttributes(stream.SafeFileHandle);
        if ((attributes
             & (FileAttributes.Directory
                | FileAttributes.Device
                | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidOperationException(
                $"Generated artifact '{path}' is not a regular file.");
        }

        return Convert.ToHexStringLower(
            await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static void ValidateOwnedArtifactPath(string path, string stateDescription)
    {
        var normalized = SiteRoute.NormalizeRelativeOutputPath(path);
        if (!string.Equals(path, normalized, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Output ownership state '{stateDescription}' contains unsafe path '{path}'.");
        }
    }

    private static void EnsureNoArtifactPathAliases(
        IReadOnlyCollection<GeneratedArtifactState> artifacts,
        string stateDescription)
    {
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var artifact in artifacts)
        {
            var identity = artifact.Path.Normalize(NormalizationForm.FormC);
            if (OperatingSystem.IsWindows())
            {
                identity = identity.ToUpperInvariant();
            }

            if (!identities.Add(identity))
            {
                throw new InvalidOperationException(
                    $"Output ownership state '{stateDescription}' contains aliased or duplicate paths.");
            }
        }
    }

    private static bool IsSha256(string value) =>
        value.Length == 64
        && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static async Task WriteNewFileAsync(
        string path,
        byte[] contents,
        CancellationToken cancellationToken)
    {
        var attributes = GetAttributes(path);
        if (attributes is not null)
        {
            EnsureNotNameSurrogateReparsePoint(path, attributes.Value);
            if ((attributes.Value & FileAttributes.Directory) != 0)
            {
                throw new IOException($"Output file path '{path}' is occupied by a directory.");
            }

            File.Delete(path);
        }

        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            FileOptions.Asynchronous);
        EnsureOpenedFilePath(stream, path);
        await stream.WriteAsync(contents, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteBundledFaviconAssetsAsync(string outputRoot, RenderContext configuration, List<string> generated, CancellationToken cancellationToken)
    {
        foreach (var assetFile in BundledFaviconAssets)
        {
            await WriteBinaryAssetAsync(
                    outputRoot,
                    configuration.Routes.Favicon(assetFile).RelativeOutputPath,
                    LoadBundledBytes(configuration.FaviconSourceDirectory, assetFile, "favicon asset"),
                    generated,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static byte[] LoadBundledBytes(string sourceDirectory, string fileName, string assetDescription)
    {
        var sourcePath = Path.Combine(sourceDirectory, fileName);
        if (!File.Exists(sourcePath))
        {
            throw new InvalidOperationException($"Bundled {assetDescription} '{sourcePath}' does not exist.");
        }

        return File.ReadAllBytes(sourcePath);
    }

    private static async Task<byte[]> BuildDefaultSocialImageAsync(RenderContext configuration, CancellationToken cancellationToken)
    {
        var generator = new SocialImageGenerator(LoadBundledBytes(configuration.FaviconSourceDirectory, SocialImageSourceFileName, "favicon asset"));
        return await generator.BuildSiteImageAsync(configuration.Site.Title, configuration.Theme.DefaultSocialSubtitle, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> BuildPostSocialImageAsync(RenderContext configuration, MarkdownPost post, CancellationToken cancellationToken)
    {
        var generator = new SocialImageGenerator(LoadBundledBytes(configuration.FaviconSourceDirectory, SocialImageSourceFileName, "favicon asset"));
        return await generator.BuildPostImageAsync(configuration.Site.Title, post.FrontMatter.Title, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> BuildContentSocialImageAsync(
        RenderContext configuration,
        IntegratedContentPage page,
        CancellationToken cancellationToken)
    {
        var generator = new SocialImageGenerator(
            LoadBundledBytes(
                configuration.FaviconSourceDirectory,
                SocialImageSourceFileName,
                "favicon asset"));
        return await generator.BuildPostImageAsync(
                configuration.Site.Title,
                page.Metadata.Title ?? page.EntryId.Value,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static string SafeCombine(string root, string relativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(normalizedRoot, PathComparison))
        {
            throw new InvalidOperationException($"Output path '{relativePath}' escapes output directory.");
        }

        return fullPath;
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static void CreateSafeDirectory(string root, string directory)
    {
        EnsureNotNameSurrogateReparsePoint(root);
        var relativePath = Path.GetRelativePath(root, directory);
        var current = root;
        foreach (var segment in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (File.Exists(current))
            {
                throw new IOException($"Output directory path '{current}' is occupied by a file.");
            }

            if (!Directory.Exists(current))
            {
                Directory.CreateDirectory(current);
            }

            EnsureNotNameSurrogateReparsePoint(current);
        }
    }

    internal static void EnsureContainedPathHasNoNameSurrogateReparsePoints(
        string root,
        string path)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(path);
        var relativePath = Path.GetRelativePath(fullRoot, fullPath);
        if (Path.IsPathRooted(relativePath)
            || relativePath == ".."
            || relativePath.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal)
            || relativePath.StartsWith(
                $"..{Path.AltDirectorySeparatorChar}",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Path '{path}' escapes root '{fullRoot}'.");
        }

        EnsureNotNameSurrogateReparsePoint(fullRoot);
        var current = fullRoot;
        foreach (var segment in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            EnsureNotNameSurrogateReparsePoint(current);
        }
    }

    private static void EnsureNotNameSurrogateReparsePoint(string path)
    {
        var attributes = GetAttributes(path);
        if (attributes is not null)
        {
            EnsureNotNameSurrogateReparsePoint(path, attributes.Value);
        }
    }

    private static void EnsureNotNameSurrogateReparsePoint(
        string path,
        FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) == 0)
        {
            return;
        }

        FileSystemInfo info = (attributes & FileAttributes.Directory) != 0
            ? new DirectoryInfo(path)
            : new FileInfo(path);
        if (info.LinkTarget is not null)
        {
            throw new InvalidOperationException(
                $"Output path '{path}' contains a symbolic link or name-surrogate reparse point.");
        }
    }

    private static FileAttributes? GetAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static void EnsureOpenedFilePath(FileStream stream, string expectedPath)
    {
        if (!string.Equals(
                Path.GetFullPath(stream.Name),
                Path.GetFullPath(expectedPath),
                PathComparison))
        {
            throw new InvalidOperationException(
                $"Opened file path '{stream.Name}' does not match expected path '{expectedPath}'.");
        }

        EnsureNotNameSurrogateReparsePoint(expectedPath);
    }

    private sealed class OutputTransaction
        : IAsyncDisposable
    {
        private const string RetainedBackupDiagnosticId = "LST001";
        private const string RetainedStagingRegistrationDiagnosticId = "LST002";
        private const string RetainedOwnershipStateRegistrationDiagnosticId = "LST003";
        private static readonly TimeSpan LockAcquisitionTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(50);
        private const int WindowsFileLockRetryCount = 4;
        private static readonly TimeSpan WindowsFileLockRetryDelay = TimeSpan.FromMilliseconds(50);
        private readonly string _outputRoot;
        private readonly string _parentRoot;
        private readonly string _backupRoot;
        private readonly string _outputIdentity;
        private readonly string _ownershipStatePath;
        private readonly string _pendingOwnershipStatePath;
        private readonly OutputLock _outputLock;
        private readonly List<FileSystemMetadata> _copiedDirectoryMetadata = [];
        private readonly List<FileSystemMetadata> _copiedFileMetadata = [];
        private readonly List<SiteDiagnostic> _diagnostics = [];
        private OutputOwnershipState? _previousOwnershipState;
        private bool _pendingOwnershipStateRegistered;

        private OutputTransaction(
            string outputRoot,
            string parentRoot,
            string stagingRoot,
            string backupRoot,
            string outputIdentity,
            string ownershipStatePath,
            string pendingOwnershipStatePath,
            OutputLock outputLock)
        {
            _outputRoot = outputRoot;
            _parentRoot = parentRoot;
            StagingRoot = stagingRoot;
            _backupRoot = backupRoot;
            _outputIdentity = outputIdentity;
            _ownershipStatePath = ownershipStatePath;
            _pendingOwnershipStatePath = pendingOwnershipStatePath;
            _outputLock = outputLock;
        }

        public string StagingRoot { get; }

        internal string OutputIdentity => _outputIdentity;

        internal async Task<string?> ReadPreviousBuildCacheKeyAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var owned = _previousOwnershipState?.Artifacts.FirstOrDefault(artifact =>
                string.Equals(artifact.Path, OutputManifestRelativePath, StringComparison.Ordinal));
            if (owned is null) return null;
            try
            {
                var path = SafeCombine(StagingRoot, OutputManifestRelativePath);
                EnsureContainedPathHasNoNameSurrogateReparsePoints(Path.GetPathRoot(path)!, path);
                await using var file = BuildInputFingerprint.OpenVerifiedContainedRead(StagingRoot, path, asynchronous: true);
                using var buffer = new MemoryStream();
                await file.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                var bytes = buffer.ToArray();
                if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != owned.Sha256) return null;
                using var document = JsonDocument.Parse(bytes);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("version", out var version) || !version.TryGetInt32(out var number) || number != OutputManifestVersion
                    || !root.TryGetProperty("buildCache", out var cache) || cache.ValueKind != JsonValueKind.String) return null;
                var digest = cache.GetString();
                return digest is not null && IsSha256(digest) && digest == digest.ToLowerInvariant() ? digest : null;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or InvalidOperationException or ArgumentException or JsonException
                or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return null;
            }
        }

        public IReadOnlyList<SiteDiagnostic> Diagnostics => _diagnostics;

        public bool RetainedRecoveryState => _diagnostics.Count != 0;

        public static async Task<OutputTransaction> CreateAsync(
            string outputRoot,
            bool preserveExisting,
            CancellationToken cancellationToken)
        {
            outputRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputRoot));
            var parentRoot = Path.GetDirectoryName(outputRoot);
            var outputName = Path.GetFileName(outputRoot);
            if (parentRoot is null || string.IsNullOrEmpty(outputName))
            {
                throw new InvalidOperationException("The file system root cannot be used as the output directory.");
            }

            CreateSafeAbsoluteDirectory(parentRoot);
            var ownershipScope = CreateOwnershipScope(parentRoot, outputName);
            var lockIdentity = CreateLockIdentity(outputRoot);
            var outputIdentity = CreateOutputIdentity(ownershipScope);
            var outputLock = await OutputLock.AcquireAsync(
                    Path.Combine(
                        parentRoot,
                        $".lithosharp-lock-{lockIdentity}.lock"),
                    lockIdentity,
                    ownershipScope,
                    cancellationToken)
                .ConfigureAwait(false);
            var suffix = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            var stagingRoot = Path.Combine(
                parentRoot,
                $".lithosharp-staging-{lockIdentity}-{suffix}");
            var backupRoot = Path.Combine(
                parentRoot,
                $".lithosharp-backup-{lockIdentity}-{suffix}");
            var ownershipStatePath = Path.Combine(
                parentRoot,
                $".lithosharp-ownership-{lockIdentity}-{outputIdentity}.json");
            var pendingOwnershipStatePath = Path.Combine(
                parentRoot,
                $".lithosharp-ownership-pending-{lockIdentity}-{outputIdentity}-{suffix}.json");
            EnsureOwnedSiblingPath(
                parentRoot,
                stagingRoot,
                "staging",
                lockIdentity);
            EnsureOwnedSiblingPath(
                parentRoot,
                backupRoot,
                "backup",
                lockIdentity);
            EnsureOwnershipStatePath(
                parentRoot,
                ownershipStatePath,
                lockIdentity,
                outputIdentity,
                pending: false);
            EnsureOwnershipStatePath(
                parentRoot,
                pendingOwnershipStatePath,
                lockIdentity,
                outputIdentity,
                pending: true);

            var transaction = new OutputTransaction(
                outputRoot,
                parentRoot,
                stagingRoot,
                backupRoot,
                outputIdentity,
                ownershipStatePath,
                pendingOwnershipStatePath,
                outputLock);
            var initialized = false;
            try
            {
                await transaction.RecoverRegisteredTransactionsAsync()
                    .ConfigureAwait(false);
                transaction._previousOwnershipState =
                    await ReadOwnershipStateAsync(
                            ownershipStatePath,
                            outputIdentity,
                            cancellationToken)
                        .ConfigureAwait(false);
                var outputAttributes = GetAttributes(outputRoot);
                if (outputAttributes is not null
                    && (outputAttributes.Value & FileAttributes.Directory) == 0)
                {
                    throw new IOException($"Output path '{outputRoot}' is not a directory.");
                }

                if (outputAttributes is not null)
                {
                    EnsureTreeContainsNoNameSurrogateReparsePoints(outputRoot);
                }

                await outputLock.RegisterStagingAsync(stagingRoot).ConfigureAwait(false);
                CreateTransactionDirectory(stagingRoot);
                EnsureNotNameSurrogateReparsePoint(stagingRoot);
                if (preserveExisting && outputAttributes is not null)
                {
                    await transaction.CopyDirectoryAsync(
                            outputRoot,
                            stagingRoot,
                            cancellationToken,
                            includeRootMetadata: true)
                        .ConfigureAwait(false);
                }

                initialized = true;
                return transaction;
            }
            finally
            {
                if (!initialized)
                {
                    try
                    {
                        await transaction.CleanupAsync().ConfigureAwait(false);
                    }
                    finally
                    {
                        await outputLock.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
        }

        public async Task<IReadOnlyList<string>> RemoveStaleOwnedFilesAsync(
            IReadOnlyCollection<string> currentOwnedPaths,
            CancellationToken cancellationToken)
        {
            var removed = new List<string>();
            if (_previousOwnershipState is null)
            {
                return removed;
            }

            var current = currentOwnedPaths
                .Select(ArtifactPathIdentity)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var artifact in _previousOwnershipState.Artifacts
                         .Where(artifact => !current.Contains(ArtifactPathIdentity(artifact.Path)))
                         .OrderBy(static artifact => artifact.Path, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = artifact.Path;
                var fullPath = SafeCombine(StagingRoot, relativePath);
                var attributes = GetAttributes(fullPath);
                if (attributes is null)
                {
                    continue;
                }

                EnsureNotNameSurrogateReparsePoint(fullPath, attributes.Value);
                if ((attributes.Value & FileAttributes.Directory) != 0)
                {
                    continue;
                }

                if (!string.Equals(
                        await ComputeFileSha256Async(fullPath, cancellationToken)
                            .ConfigureAwait(false),
                        artifact.Sha256,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                _copiedFileMetadata.RemoveAll(metadata =>
                    string.Equals(
                        metadata.DestinationPath,
                        fullPath,
                        PathComparison));
                File.SetAttributes(
                    fullPath,
                    attributes.Value & ~(FileAttributes.ReadOnly | FileAttributes.System));
                File.Delete(fullPath);
                removed.Add(relativePath);
                RemoveEmptyOwnedDirectories(Path.GetDirectoryName(fullPath)!);
            }

            return removed.Order(StringComparer.Ordinal).ToArray();
        }

        public async Task PrepareOwnershipStateAsync(
            IReadOnlyCollection<string> generatedFiles,
            CancellationToken cancellationToken)
        {
            if (_pendingOwnershipStateRegistered)
            {
                throw new InvalidOperationException(
                    "Output ownership state has already been prepared.");
            }

            var artifacts = await CreateArtifactStatesAsync(
                    StagingRoot,
                    generatedFiles,
                    cancellationToken)
                .ConfigureAwait(false);
            await _outputLock.RegisterOwnershipStateAsync(_pendingOwnershipStatePath)
                .ConfigureAwait(false);
            _pendingOwnershipStateRegistered = true;
            await WriteOwnershipStateAsync(
                    _pendingOwnershipStatePath,
                    new OutputOwnershipState(_outputIdentity, artifacts),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private void RemoveEmptyOwnedDirectories(string directory)
        {
            var current = directory;
            while (!string.Equals(current, StagingRoot, PathComparison)
                   && !Directory.EnumerateFileSystemEntries(current).Any())
            {
                _copiedDirectoryMetadata.RemoveAll(metadata =>
                    string.Equals(
                        metadata.DestinationPath,
                        current,
                        PathComparison));
                Directory.Delete(current);
                current = Path.GetDirectoryName(current)
                    ?? throw new InvalidOperationException(
                        $"Staging path '{directory}' has no parent.");
            }
        }

        public async Task CommitAsync(IReadOnlyCollection<string> generatedFiles)
        {
            if (!_pendingOwnershipStateRegistered
                || GetAttributes(_pendingOwnershipStatePath) is null)
            {
                throw new InvalidOperationException(
                    "Output ownership state must be prepared before committing output.");
            }

            EnsureTreeContainsNoNameSurrogateReparsePoints(StagingRoot);
            ApplyCopiedMetadata(generatedFiles);
            EnsureNotNameSurrogateReparsePoint(StagingRoot);
            var outputAttributes = GetAttributes(_outputRoot);
            if (outputAttributes is null)
            {
                await MoveDirectoryWithRetriesAsync(StagingRoot, _outputRoot).ConfigureAwait(false);
                try
                {
                    await PromoteOwnershipStateAsync().ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is IOException
                        or UnauthorizedAccessException
                        or InvalidOperationException)
                {
                    await RollbackPromotedOutputAsync(
                            exception,
                            restoreBackup: false)
                        .ConfigureAwait(false);
                }

                await TryUnregisterPromotedStagingAsync().ConfigureAwait(false);
                return;
            }

            if ((outputAttributes.Value & FileAttributes.Directory) == 0)
            {
                throw new IOException($"Output path '{_outputRoot}' is not a directory.");
            }

            EnsureTreeContainsNoNameSurrogateReparsePoints(_outputRoot);
            RestoreCopiedSourceDirectoryMetadata();
            await _outputLock.RegisterBackupAsync(_backupRoot).ConfigureAwait(false);
            await MoveDirectoryWithRetriesAsync(_outputRoot, _backupRoot).ConfigureAwait(false);
            try
            {
                await MoveDirectoryWithRetriesAsync(StagingRoot, _outputRoot).ConfigureAwait(false);
            }
            catch (IOException commitException)
            {
                await RestorePreviousOutputAsync(commitException).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException commitException)
            {
                await RestorePreviousOutputAsync(commitException).ConfigureAwait(false);
            }

            try
            {
                await PromoteOwnershipStateAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or InvalidOperationException)
            {
                await RollbackPromotedOutputAsync(
                        exception,
                        restoreBackup: true)
                    .ConfigureAwait(false);
            }

            await TryUnregisterPromotedStagingAsync().ConfigureAwait(false);
            await TryCleanupCommittedBackupAsync(_backupRoot).ConfigureAwait(false);
        }

        public async Task CleanupAsync()
        {
            await DeleteOwnedDirectoryAsync(StagingRoot, "staging").ConfigureAwait(false);
            await _outputLock.UnregisterStagingAsync(StagingRoot).ConfigureAwait(false);
            await DeletePendingOwnershipStateAsync().ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() =>
            _outputLock.DisposeAsync();

        private async Task DeleteOwnedDirectoryAsync(string path, string kind)
        {
            EnsureOwnedSiblingPath(
                _parentRoot,
                path,
                kind,
                _outputLock.Identity);
            var attributes = GetAttributes(path);
            if (attributes is null)
            {
                return;
            }

            if ((attributes.Value & FileAttributes.Directory) == 0)
            {
                throw new IOException($"Owned {kind} path '{path}' is not a directory.");
            }

            EnsureTreeContainsNoNameSurrogateReparsePoints(path);
            PrepareOwnedTreeForDeletion(path);
            await DeleteDirectoryWithRetriesAsync(path).ConfigureAwait(false);
        }

        private async Task TryCleanupCommittedBackupAsync(string backupRoot)
        {
            try
            {
                await DeleteOwnedDirectoryAsync(backupRoot, "backup").ConfigureAwait(false);
                await _outputLock.UnregisterBackupAsync(backupRoot).ConfigureAwait(false);
            }
            catch (IOException exception)
            {
                RecordRetainedBackup(backupRoot, exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                RecordRetainedBackup(backupRoot, exception);
            }
            catch (InvalidOperationException exception)
            {
                RecordRetainedBackup(backupRoot, exception);
            }
        }

        private void RecordRetainedBackup(string backupRoot, Exception exception)
        {
            RecordCleanupDiagnostic(
                RetainedBackupDiagnosticId,
                "Committed output retained a backup for a later recovery cleanup.");
            System.Diagnostics.Trace.TraceWarning(
                "LithoSharp committed generated output but retained backup '{0}' for a later cleanup attempt: {1}",
                backupRoot,
                exception.Message);
        }

        private async Task TryUnregisterPromotedStagingAsync()
        {
            try
            {
                await _outputLock.UnregisterStagingAsync(StagingRoot).ConfigureAwait(false);
            }
            catch (IOException exception)
            {
                RecordCleanupDiagnostic(
                    RetainedStagingRegistrationDiagnosticId,
                    "Committed output retained a staging registration for a later recovery cleanup.");
                System.Diagnostics.Trace.TraceWarning(
                    "LithoSharp committed generated output but retained stale staging registration '{0}': {1}",
                    StagingRoot,
                    exception.Message);
            }
            catch (UnauthorizedAccessException exception)
            {
                RecordCleanupDiagnostic(
                    RetainedStagingRegistrationDiagnosticId,
                    "Committed output retained a staging registration for a later recovery cleanup.");
                System.Diagnostics.Trace.TraceWarning(
                    "LithoSharp committed generated output but retained stale staging registration '{0}': {1}",
                    StagingRoot,
                    exception.Message);
            }
        }

        private async Task PromoteOwnershipStateAsync()
        {
            EnsureOwnershipStatePath(
                _parentRoot,
                _pendingOwnershipStatePath,
                _outputLock.Identity,
                _outputIdentity,
                pending: true);
            EnsureOwnershipStatePath(
                _parentRoot,
                _ownershipStatePath,
                _outputLock.Identity,
                _outputIdentity,
                pending: false);
            var pendingAttributes = GetAttributes(_pendingOwnershipStatePath)
                ?? throw new IOException(
                    $"Pending output ownership state '{_pendingOwnershipStatePath}' is missing.");
            EnsureNotNameSurrogateReparsePoint(
                _pendingOwnershipStatePath,
                pendingAttributes);
            if ((pendingAttributes & FileAttributes.Directory) != 0)
            {
                throw new IOException(
                    $"Pending output ownership state '{_pendingOwnershipStatePath}' is a directory.");
            }

            var currentAttributes = GetAttributes(_ownershipStatePath);
            if (currentAttributes is not null)
            {
                EnsureNotNameSurrogateReparsePoint(
                    _ownershipStatePath,
                    currentAttributes.Value);
                if ((currentAttributes.Value & FileAttributes.Directory) != 0)
                {
                    throw new IOException(
                        $"Output ownership state '{_ownershipStatePath}' is a directory.");
                }
            }

            File.Move(
                _pendingOwnershipStatePath,
                _ownershipStatePath,
                overwrite: true);
            _pendingOwnershipStateRegistered = false;
            try
            {
                await _outputLock.UnregisterOwnershipStateAsync(
                        _pendingOwnershipStatePath)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                RecordCleanupDiagnostic(
                    RetainedOwnershipStateRegistrationDiagnosticId,
                    "Committed output retained an ownership-state registration for a later recovery cleanup.");
                System.Diagnostics.Trace.TraceWarning(
                    "LithoSharp committed generated output but retained stale ownership-state registration '{0}': {1}",
                    _pendingOwnershipStatePath,
                    exception.Message);
            }
        }

        private async Task RollbackPromotedOutputAsync(
            Exception stateException,
            bool restoreBackup)
        {
            try
            {
                await MoveDirectoryWithRetriesAsync(_outputRoot, StagingRoot)
                    .ConfigureAwait(false);
                if (restoreBackup)
                {
                    await MoveDirectoryWithRetriesAsync(_backupRoot, _outputRoot)
                        .ConfigureAwait(false);
                    await _outputLock.UnregisterBackupAsync(_backupRoot)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception rollbackException) when (
                rollbackException is IOException or UnauthorizedAccessException)
            {
                throw new IOException(
                    "Atomic output ownership-state commit failed and the previous output "
                    + $"could not be restored. The previous output remains at '{_backupRoot}'.",
                    new AggregateException(stateException, rollbackException));
            }

            throw new IOException(
                restoreBackup
                    ? "Atomic output ownership-state commit failed; the previous output was restored."
                    : "Atomic output ownership-state commit failed; the newly promoted output was removed.",
                stateException);
        }

        private async Task DeletePendingOwnershipStateAsync()
        {
            if (!_pendingOwnershipStateRegistered)
            {
                return;
            }

            EnsureOwnershipStatePath(
                _parentRoot,
                _pendingOwnershipStatePath,
                _outputLock.Identity,
                _outputIdentity,
                pending: true);
            var attributes = GetAttributes(_pendingOwnershipStatePath);
            if (attributes is not null)
            {
                EnsureNotNameSurrogateReparsePoint(
                    _pendingOwnershipStatePath,
                    attributes.Value);
                if ((attributes.Value & FileAttributes.Directory) != 0)
                {
                    throw new IOException(
                        $"Pending output ownership state '{_pendingOwnershipStatePath}' is a directory.");
                }

                File.SetAttributes(
                    _pendingOwnershipStatePath,
                    attributes.Value & ~(FileAttributes.ReadOnly | FileAttributes.System));
                File.Delete(_pendingOwnershipStatePath);
            }

            await _outputLock.UnregisterOwnershipStateAsync(_pendingOwnershipStatePath)
                .ConfigureAwait(false);
            _pendingOwnershipStateRegistered = false;
        }

        private async Task RestorePreviousOutputAsync(Exception commitException)
        {
            try
            {
                await MoveDirectoryWithRetriesAsync(_backupRoot, _outputRoot).ConfigureAwait(false);
            }
            catch (IOException rollbackException)
            {
                throw CreateRollbackFailure(commitException, rollbackException);
            }
            catch (UnauthorizedAccessException rollbackException)
            {
                throw CreateRollbackFailure(commitException, rollbackException);
            }

            await _outputLock.UnregisterBackupAsync(_backupRoot).ConfigureAwait(false);
            throw new IOException(
                "Atomic output commit failed; the previous output was restored. "
                + "The platform did not permit the required same-volume directory rename.",
                commitException);
        }

        private IOException CreateRollbackFailure(
            Exception commitException,
            Exception rollbackException) =>
            new(
                $"Atomic output commit failed and the previous output could not be restored. "
                + $"The previous output remains at '{_backupRoot}'.",
                new AggregateException(commitException, rollbackException));

        private async Task CopyDirectoryAsync(
            string sourceRoot,
            string destinationRoot,
            CancellationToken cancellationToken,
            bool includeRootMetadata = false)
        {
            var sourceRootMetadata = FileSystemMetadata.Capture(
                sourceRoot,
                destinationRoot,
                isDirectory: true);
            if (includeRootMetadata)
            {
                _copiedDirectoryMetadata.Add(sourceRootMetadata);
            }

            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(sourceRoot))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var attributes = File.GetAttributes(entry);
                    EnsureNotNameSurrogateReparsePoint(entry, attributes);
                    var destination = SafeCombine(
                        destinationRoot,
                        Path.GetRelativePath(sourceRoot, entry));
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        CreateSafeDirectory(destinationRoot, destination);
                        _copiedDirectoryMetadata.Add(
                            FileSystemMetadata.Capture(
                                entry,
                                destination,
                                isDirectory: true));
                        await CopyDirectoryAsync(
                                entry,
                                destination,
                                cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    var metadata = FileSystemMetadata.Capture(
                        entry,
                        destination,
                        isDirectory: false);
                    CreateSafeDirectory(destinationRoot, Path.GetDirectoryName(destination)!);
                    try
                    {
                        await using var source = new FileStream(
                            entry,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.Read,
                            bufferSize: 81920,
                            FileOptions.Asynchronous | FileOptions.SequentialScan);
                        EnsureOpenedFilePath(source, entry);
                        await using var target = new FileStream(
                            destination,
                            FileMode.CreateNew,
                            FileAccess.Write,
                            FileShare.None,
                            bufferSize: 81920,
                            FileOptions.Asynchronous);
                        EnsureOpenedFilePath(target, destination);
                        await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        metadata.RestoreSourceAccessTime();
                    }

                    _copiedFileMetadata.Add(metadata);
                }
            }
            finally
            {
                sourceRootMetadata.RestoreSourceAccessTime();
            }
        }

        private static void CreateSafeAbsoluteDirectory(string directory)
        {
            var fullPath = Path.GetFullPath(directory);
            var root = Path.GetPathRoot(fullPath)
                ?? throw new InvalidOperationException($"Path '{directory}' has no file system root.");
            var current = root;
            foreach (var segment in fullPath[root.Length..].Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if (File.Exists(current))
                {
                    throw new IOException($"Output parent path '{current}' is occupied by a file.");
                }

                if (!Directory.Exists(current))
                {
                    Directory.CreateDirectory(current);
                }

                EnsureNotNameSurrogateReparsePoint(current);
            }
        }

        private static void CreateTransactionDirectory(string path)
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(path);
                RestrictTransactionPathToOwner(path, isDirectory: true);
                return;
            }

            Directory.CreateDirectory(
                path,
                UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute);
            RestrictTransactionPathToOwner(path, isDirectory: true);
        }

        internal static void RestrictTransactionPathToOwner(
            string path,
            bool isDirectory)
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    path,
                    isDirectory
                        ? UnixFileMode.UserRead
                          | UnixFileMode.UserWrite
                          | UnixFileMode.UserExecute
                        : UnixFileMode.UserRead | UnixFileMode.UserWrite);
                return;
            }

            RestrictWindowsTransactionPathToOwner(path, isDirectory);
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static void RestrictWindowsTransactionPathToOwner(
            string path,
            bool isDirectory)
        {
            var owner = WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException(
                    "The current Windows identity has no security identifier.");
            var inheritance = isDirectory
                ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
                : InheritanceFlags.None;
            FileSystemSecurity security = isDirectory
                ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
                : new FileInfo(path).GetAccessControl(AccessControlSections.Access);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (FileSystemAccessRule rule in security.GetAccessRules(
                         includeExplicit: true,
                         includeInherited: false,
                         typeof(SecurityIdentifier)))
            {
                security.RemoveAccessRuleSpecific(rule);
            }

            security.AddAccessRule(new FileSystemAccessRule(
                owner,
                FileSystemRights.FullControl,
                inheritance,
                PropagationFlags.None,
                AccessControlType.Allow));
            if (isDirectory)
            {
                new DirectoryInfo(path).SetAccessControl((DirectorySecurity)security);
            }
            else
            {
                new FileInfo(path).SetAccessControl((FileSecurity)security);
            }
        }

        internal static void EnsureTransactionPathIsOwnerRestricted(
            string path,
            bool isDirectory)
        {
            if (!OperatingSystem.IsWindows())
            {
                var expected = isDirectory
                    ? UnixFileMode.UserRead
                      | UnixFileMode.UserWrite
                      | UnixFileMode.UserExecute
                    : UnixFileMode.UserRead | UnixFileMode.UserWrite;
                if (File.GetUnixFileMode(path) != expected)
                {
                    throw new InvalidOperationException(
                        $"Transaction path '{path}' is not restricted to its owner.");
                }

                return;
            }

            EnsureWindowsTransactionPathIsOwnerRestricted(path, isDirectory);
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static void EnsureWindowsTransactionPathIsOwnerRestricted(
            string path,
            bool isDirectory)
        {
            var owner = WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException(
                    "The current Windows identity has no security identifier.");
            FileSystemSecurity security = isDirectory
                ? new DirectoryInfo(path).GetAccessControl(
                    AccessControlSections.Access | AccessControlSections.Owner)
                : new FileInfo(path).GetAccessControl(
                    AccessControlSections.Access | AccessControlSections.Owner);
            if (!security.AreAccessRulesProtected
                || !owner.Equals(security.GetOwner(typeof(SecurityIdentifier))))
            {
                throw new InvalidOperationException(
                    $"Transaction path '{path}' is not restricted to its owner.");
            }

            var rules = security.GetAccessRules(
                    includeExplicit: true,
                    includeInherited: true,
                    typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .ToArray();
            if (rules.Length == 0
                || rules.Any(rule =>
                    rule.AccessControlType != AccessControlType.Allow
                    || !owner.Equals(rule.IdentityReference)))
            {
                throw new InvalidOperationException(
                    $"Transaction path '{path}' is not restricted to its owner.");
            }
        }

        private static void PrepareOwnedTreeForDeletion(string root)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(root))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    PrepareOwnedTreeForDeletion(entry);
                }
                else
                {
                    RestrictTransactionPathToOwner(entry, isDirectory: false);
                }

                File.SetAttributes(
                    entry,
                    attributes & ~(FileAttributes.ReadOnly | FileAttributes.System));
            }

            RestrictTransactionPathToOwner(root, isDirectory: true);
            var rootAttributes = File.GetAttributes(root);
            File.SetAttributes(
                root,
                rootAttributes & ~(FileAttributes.ReadOnly | FileAttributes.System));
        }

        private static void EnsureTreeContainsNoNameSurrogateReparsePoints(string root)
        {
            EnsureNotNameSurrogateReparsePoint(root);
            foreach (var entry in Directory.EnumerateFileSystemEntries(root))
            {
                var attributes = File.GetAttributes(entry);
                EnsureNotNameSurrogateReparsePoint(entry, attributes);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    EnsureTreeContainsNoNameSurrogateReparsePoints(entry);
                }
            }
        }

        private void ApplyCopiedMetadata(IReadOnlyCollection<string> generatedFiles)
        {
            var generatedPaths = generatedFiles.ToHashSet(
                OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal);
            foreach (var metadata in _copiedFileMetadata)
            {
                metadata.Apply(
                    preserveTimestamps: !generatedPaths.Contains(metadata.DestinationPath));
            }

            for (var index = _copiedDirectoryMetadata.Count - 1; index >= 0; index--)
            {
                _copiedDirectoryMetadata[index].Apply();
            }
        }

        private void RestoreCopiedSourceDirectoryMetadata()
        {
            for (var index = _copiedDirectoryMetadata.Count - 1; index >= 0; index--)
            {
                _copiedDirectoryMetadata[index].RestoreSource();
            }
        }

        private async Task RecoverRegisteredTransactionsAsync()
        {
            await RecoverRegisteredOwnershipStatesAsync().ConfigureAwait(false);
            var registeredBackups = _outputLock.RegisteredBackups.ToArray();
            string? restoredBackup = null;
            if (GetAttributes(_outputRoot) is null)
            {
                for (var index = registeredBackups.Length - 1; index >= 0; index--)
                {
                    var backup = registeredBackups[index];
                    EnsureOwnedSiblingPath(
                        _parentRoot,
                        backup,
                        "backup",
                        _outputLock.Identity);
                    if (GetAttributes(backup) is null)
                    {
                        await TryUnregisterRecoveredBackupAsync(backup).ConfigureAwait(false);
                        continue;
                    }

                    EnsureTreeContainsNoNameSurrogateReparsePoints(backup);
                    await MoveDirectoryWithRetriesAsync(backup, _outputRoot).ConfigureAwait(false);
                    await TryUnregisterRecoveredBackupAsync(backup).ConfigureAwait(false);
                    restoredBackup = backup;
                    break;
                }
            }

            foreach (var backup in registeredBackups)
            {
                if (string.Equals(backup, restoredBackup, PathComparison))
                {
                    continue;
                }

                if (GetAttributes(backup) is null)
                {
                    await TryUnregisterRecoveredBackupAsync(backup).ConfigureAwait(false);
                    continue;
                }

                await TryCleanupCommittedBackupAsync(backup).ConfigureAwait(false);
            }

            foreach (var staging in _outputLock.RegisteredStaging.ToArray())
            {
                EnsureOwnedSiblingPath(
                    _parentRoot,
                    staging,
                    "staging",
                    _outputLock.Identity);
                try
                {
                    await DeleteOwnedDirectoryAsync(staging, "staging").ConfigureAwait(false);
                    await _outputLock.UnregisterStagingAsync(staging).ConfigureAwait(false);
                }
                catch (IOException exception)
                {
                    RecordRetainedStaging(staging, exception);
                }
                catch (UnauthorizedAccessException exception)
                {
                    RecordRetainedStaging(staging, exception);
                }
                catch (InvalidOperationException exception)
                {
                    RecordRetainedStaging(staging, exception);
                }
            }
        }

        private async Task RecoverRegisteredOwnershipStatesAsync()
        {
            foreach (var pendingPath in _outputLock.RegisteredOwnershipStates.ToArray())
            {
                EnsureOwnershipStatePath(
                    _parentRoot,
                    pendingPath,
                    _outputLock.Identity,
                    _outputIdentity,
                    pending: true);
                var suffix = GetPendingOwnershipStateSuffix(pendingPath);
                var stagingPath = Path.Combine(
                    _parentRoot,
                    $".lithosharp-staging-{_outputLock.Identity}-{suffix}");
                EnsureOwnedSiblingPath(
                    _parentRoot,
                    stagingPath,
                    "staging",
                    _outputLock.Identity);
                var outputExists = GetAttributes(_outputRoot) is not null;
                var stagingExists = GetAttributes(stagingPath) is not null;
                if (outputExists && !stagingExists)
                {
                    EnsureTreeContainsNoNameSurrogateReparsePoints(_outputRoot);
                    var pendingState = await ReadOwnershipStateAsync(
                            pendingPath,
                            _outputIdentity,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    if (pendingState is not null)
                    {
                        await EnsureOutputMatchesOwnershipStateAsync(pendingState)
                            .ConfigureAwait(false);
                        File.Move(
                            pendingPath,
                            _ownershipStatePath,
                            overwrite: true);
                    }
                    else
                    {
                        var promotedState = await ReadOwnershipStateAsync(
                                _ownershipStatePath,
                                _outputIdentity,
                                CancellationToken.None)
                            .ConfigureAwait(false)
                            ?? throw new InvalidOperationException(
                                $"Registered output ownership state '{pendingPath}' is missing.");
                        await EnsureOutputMatchesOwnershipStateAsync(promotedState)
                            .ConfigureAwait(false);
                    }
                }
                else
                {
                    DeleteRegisteredOwnershipState(pendingPath);
                }

                try
                {
                    await _outputLock.UnregisterOwnershipStateAsync(pendingPath)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    RecordCleanupDiagnostic(
                        RetainedOwnershipStateRegistrationDiagnosticId,
                        "Committed output retained an ownership-state registration for a later recovery cleanup.");
                    System.Diagnostics.Trace.TraceWarning(
                        "LithoSharp retained stale ownership-state registration '{0}' for a later cleanup attempt: {1}",
                        pendingPath,
                        exception.Message);
                }
            }
        }

        private void RecordRetainedStaging(string staging, Exception exception)
        {
            RecordCleanupDiagnostic(
                RetainedStagingRegistrationDiagnosticId,
                "Committed output retained a staging registration for a later recovery cleanup.");
            TraceRetainedStaging(staging, exception);
        }

        private async Task TryUnregisterRecoveredBackupAsync(string backup)
        {
            try
            {
                await _outputLock.UnregisterBackupAsync(backup).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                RecordRetainedBackup(backup, exception);
            }
        }

        private void RecordCleanupDiagnostic(string id, string message)
        {
            if (_diagnostics.All(diagnostic => diagnostic.Id != id))
            {
                _diagnostics.Add(new SiteDiagnostic(id, SiteDiagnosticSeverity.Warning, message));
            }
        }

        private async Task EnsureOutputMatchesOwnershipStateAsync(
            OutputOwnershipState state)
        {
            foreach (var artifact in state.Artifacts)
            {
                var path = SafeCombine(_outputRoot, artifact.Path);
                var attributes = GetAttributes(path)
                    ?? throw new InvalidOperationException(
                        $"Promoted output is missing generated artifact '{artifact.Path}'.");
                EnsureNotNameSurrogateReparsePoint(path, attributes);
                if ((attributes & FileAttributes.Directory) != 0
                    || !string.Equals(
                        await ComputeFileSha256Async(path, CancellationToken.None)
                            .ConfigureAwait(false),
                        artifact.Sha256,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Promoted output artifact '{artifact.Path}' does not match its ownership state.");
                }
            }
        }

        private static void DeleteRegisteredOwnershipState(string path)
        {
            var attributes = GetAttributes(path);
            if (attributes is null)
            {
                return;
            }

            EnsureNotNameSurrogateReparsePoint(path, attributes.Value);
            if ((attributes.Value & FileAttributes.Directory) != 0)
            {
                throw new InvalidOperationException(
                    $"Registered output ownership state '{path}' is a directory.");
            }

            RestrictTransactionPathToOwner(path, isDirectory: false);
            File.SetAttributes(
                path,
                attributes.Value & ~(FileAttributes.ReadOnly | FileAttributes.System));
            File.Delete(path);
        }

        private static string GetPendingOwnershipStateSuffix(string path)
        {
            var fileName = Path.GetFileName(path);
            var extensionLength = ".json".Length;
            return fileName[(fileName.LastIndexOf('-') + 1)..^extensionLength];
        }

        private static void TraceRetainedStaging(string stagingRoot, Exception exception) =>
            System.Diagnostics.Trace.TraceWarning(
                "LithoSharp retained registered staging directory '{0}' for a later cleanup attempt: {1}",
                stagingRoot,
                exception.Message);

        private static string CreateLockIdentity(string outputRoot)
        {
            var normalizedPath = outputRoot
                .Normalize(NormalizationForm.FormC)
                .ToUpperInvariant();
            return Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)))
                .ToLowerInvariant();
        }

        private static string CreateOwnershipScope(
            string parentRoot,
            string outputName)
        {
            var canonicalParent = ResolveExistingDirectoryPath(parentRoot);
            var requestedOutput = Path.Combine(canonicalParent, outputName);
            var entries = Directory.EnumerateFileSystemEntries(canonicalParent).ToArray();
            var exact = entries.SingleOrDefault(entry =>
                string.Equals(Path.GetFileName(entry), outputName, StringComparison.Ordinal));
            if (exact is not null)
            {
                return Path.TrimEndingDirectorySeparator(Path.GetFullPath(exact));
            }

            if (Directory.Exists(requestedOutput))
            {
                var aliases = entries
                    .Where(Directory.Exists)
                    .Where(entry => FileSystemNamesAlias(
                        Path.GetFileName(entry),
                        outputName))
                    .ToArray();
                if (aliases.Length != 1)
                {
                    throw new InvalidOperationException(
                        $"Output path '{requestedOutput}' has an ambiguous physical identity.");
                }

                return Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(aliases[0]));
            }

            var prospectiveName = OperatingSystem.IsMacOS()
                ? outputName.Normalize(NormalizationForm.FormD)
                : outputName;
            return Path.Combine(canonicalParent, prospectiveName);
        }

        private static string ResolveExistingDirectoryPath(string path)
        {
            var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var root = Path.GetPathRoot(fullPath)
                ?? throw new InvalidOperationException($"Path '{path}' has no file system root.");
            var current = root;
            foreach (var segment in fullPath[root.Length..].Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                var entries = Directory.EnumerateDirectories(current).ToArray();
                var exact = entries.SingleOrDefault(entry =>
                    string.Equals(Path.GetFileName(entry), segment, StringComparison.Ordinal));
                if (exact is not null)
                {
                    current = exact;
                    continue;
                }

                var requested = Path.Combine(current, segment);
                if (!Directory.Exists(requested))
                {
                    throw new DirectoryNotFoundException(
                        $"Output parent directory '{requested}' was not found.");
                }

                var aliases = entries
                    .Where(entry => FileSystemNamesAlias(
                        Path.GetFileName(entry),
                        segment))
                    .ToArray();
                if (aliases.Length != 1)
                {
                    throw new InvalidOperationException(
                        $"Output parent path '{requested}' has an ambiguous physical identity.");
                }

                current = aliases[0];
            }

            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
        }

        private static bool FileSystemNamesAlias(string left, string right)
        {
            if (OperatingSystem.IsWindows())
            {
                return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
            }

            if (OperatingSystem.IsMacOS())
            {
                return string.Equals(
                    left.Normalize(NormalizationForm.FormD),
                    right.Normalize(NormalizationForm.FormD),
                    StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(left, right, StringComparison.Ordinal);
        }

        private static string CreateOutputIdentity(string canonicalOutputRoot) =>
            Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonicalOutputRoot)));

        private static string ArtifactPathIdentity(string relativePath)
        {
            var identity = relativePath.Normalize(NormalizationForm.FormC);
            return OperatingSystem.IsWindows()
                ? identity.ToUpperInvariant()
                : identity;
        }

        private static void EnsureOwnershipStatePath(
            string parentRoot,
            string path,
            string lockIdentity,
            string outputIdentity,
            bool pending)
        {
            var fullPath = Path.GetFullPath(path);
            var fileName = Path.GetFileName(fullPath);
            var validName = pending
                ? IsPendingOwnershipStateName(
                    fileName,
                    lockIdentity,
                    outputIdentity)
                : string.Equals(
                    fileName,
                    $".lithosharp-ownership-{lockIdentity}-{outputIdentity}.json",
                    StringComparison.Ordinal);
            if (!string.Equals(
                    Path.GetDirectoryName(fullPath),
                    parentRoot,
                    PathComparison)
                || !validName)
            {
                throw new InvalidOperationException(
                    $"Refusing to operate on unresolved output ownership state path '{path}'.");
            }
        }

        private static bool IsPendingOwnershipStateName(
            string fileName,
            string lockIdentity,
            string outputIdentity)
        {
            var prefix =
                $".lithosharp-ownership-pending-{lockIdentity}-{outputIdentity}-";
            if (!fileName.StartsWith(prefix, StringComparison.Ordinal)
                || !fileName.EndsWith(".json", StringComparison.Ordinal))
            {
                return false;
            }

            var suffix = fileName[prefix.Length..^".json".Length];
            return suffix.Length == 32
                   && suffix.All(static character =>
                       character is >= '0' and <= '9' or >= 'a' and <= 'f');
        }

        private static void EnsureOwnedSiblingPath(
            string parentRoot,
            string path,
            string kind,
            string lockIdentity)
        {
            var fullPath = Path.GetFullPath(path);
            var expectedPrefix =
                $".lithosharp-{kind}-{lockIdentity}-";
            if (!string.Equals(
                    Path.GetDirectoryName(fullPath),
                    parentRoot,
                    PathComparison)
                || !Path.GetFileName(fullPath).StartsWith(
                    expectedPrefix,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Refusing to operate on unresolved {kind} path '{path}'.");
            }
        }

        private static async Task MoveDirectoryWithRetriesAsync(
            string source,
            string destination)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    Directory.Move(source, destination);
                    return;
                }
                catch (IOException) when (
                    OperatingSystem.IsWindows()
                    && attempt < WindowsFileLockRetryCount)
                {
                }
                catch (UnauthorizedAccessException) when (
                    OperatingSystem.IsWindows()
                    && attempt < WindowsFileLockRetryCount)
                {
                }

                await Task.Delay(WindowsFileLockRetryDelay).ConfigureAwait(false);
            }
        }

        private static async Task DeleteDirectoryWithRetriesAsync(string path)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    Directory.Delete(path, recursive: true);
                    return;
                }
                catch (IOException) when (
                    OperatingSystem.IsWindows()
                    && attempt < WindowsFileLockRetryCount)
                {
                }
                catch (UnauthorizedAccessException) when (
                    OperatingSystem.IsWindows()
                    && attempt < WindowsFileLockRetryCount)
                {
                }

                await Task.Delay(WindowsFileLockRetryDelay).ConfigureAwait(false);
            }
        }

        private sealed class OutputLock : IAsyncDisposable
        {
            private readonly FileStream _stream;
            private readonly List<TransactionRegistration> _registeredBackups;
            private readonly List<TransactionRegistration> _registeredStaging;
            private readonly List<TransactionRegistration> _registeredOwnershipStates;
            private readonly string _scope;

            private OutputLock(
                FileStream stream,
                string identity,
                string scope,
                TransactionState state)
            {
                _stream = stream;
                Identity = identity;
                _scope = scope;
                _registeredBackups = state.Backups;
                _registeredStaging = state.Staging;
                _registeredOwnershipStates = state.OwnershipStates;
            }

            public string Identity { get; }

            public IReadOnlyList<string> RegisteredBackups =>
                _registeredBackups
                    .Where(registration => ScopeMatches(registration.Scope))
                    .Select(registration => registration.Path)
                    .ToArray();

            public IReadOnlyList<string> RegisteredStaging =>
                _registeredStaging
                    .Where(registration => ScopeMatches(registration.Scope))
                    .Select(registration => registration.Path)
                    .ToArray();

            public IReadOnlyList<string> RegisteredOwnershipStates =>
                _registeredOwnershipStates
                    .Where(registration => ScopeMatches(registration.Scope))
                    .Select(registration => registration.Path)
                    .ToArray();

            public static async Task<OutputLock> AcquireAsync(
                string lockPath,
                string identity,
                string scope,
                CancellationToken cancellationToken)
            {
                PrepareWindowsLockFile(lockPath);
                var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                IOException? lastContention = null;
                while (System.Diagnostics.Stopwatch.GetElapsedTime(startedAt)
                       < LockAcquisitionTimeout)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var attributes = GetAttributes(lockPath);
                    if (attributes is not null)
                    {
                        if ((attributes.Value & FileAttributes.Directory) != 0)
                        {
                            throw new IOException(
                                $"Output transaction lock path '{lockPath}' is a directory.");
                        }

                        EnsureNotNameSurrogateReparsePoint(lockPath, attributes.Value);
                    }

                    FileStream? stream = null;
                    try
                    {
                        stream = CreateLockStream(lockPath);
                        EnsureOpenedFilePath(stream, lockPath);
                        if (!OperatingSystem.IsWindows())
                        {
                            RestrictTransactionPathToOwner(
                                lockPath,
                                isDirectory: false);
                        }

                        return new OutputLock(
                            stream,
                            identity,
                            scope,
                            ReadTransactionState(stream, scope));
                    }
                    catch (IOException exception)
                    {
                        stream?.Dispose();
                        lastContention = exception;
                    }
                    catch (InvalidOperationException)
                    {
                        stream?.Dispose();
                        throw;
                    }

                    await Task.Delay(LockRetryDelay, cancellationToken).ConfigureAwait(false);
                }

                throw new TimeoutException(
                    $"Timed out acquiring the output transaction lock for '{lockPath}'.",
                    lastContention);
            }

            private static void PrepareWindowsLockFile(string lockPath)
            {
                if (!OperatingSystem.IsWindows() || GetAttributes(lockPath) is not null)
                {
                    return;
                }

                try
                {
                    using (new FileStream(
                               lockPath,
                               FileMode.CreateNew,
                               FileAccess.ReadWrite,
                               FileShare.None))
                    {
                    }
                }
                catch (IOException) when (GetAttributes(lockPath) is not null)
                {
                    return;
                }

                RestrictTransactionPathToOwner(lockPath, isDirectory: false);
            }

            public async Task RegisterBackupAsync(string backupRoot)
            {
                if (!_registeredBackups.Any(registration =>
                        ScopeMatches(registration.Scope)
                        && registration.Path == backupRoot))
                {
                    _registeredBackups.Add(new TransactionRegistration(_scope, backupRoot));
                    await PersistAsync().ConfigureAwait(false);
                }
            }

            public async Task UnregisterBackupAsync(string backupRoot)
            {
                if (_registeredBackups.RemoveAll(registration =>
                        ScopeMatches(registration.Scope)
                        && registration.Path == backupRoot) > 0)
                {
                    await PersistAsync().ConfigureAwait(false);
                }
            }

            public async Task RegisterStagingAsync(string stagingRoot)
            {
                if (!_registeredStaging.Any(registration =>
                        ScopeMatches(registration.Scope)
                        && registration.Path == stagingRoot))
                {
                    _registeredStaging.Add(new TransactionRegistration(_scope, stagingRoot));
                    await PersistAsync().ConfigureAwait(false);
                }
            }

            public async Task UnregisterStagingAsync(string stagingRoot)
            {
                if (_registeredStaging.RemoveAll(registration =>
                        ScopeMatches(registration.Scope)
                        && registration.Path == stagingRoot) > 0)
                {
                    await PersistAsync().ConfigureAwait(false);
                }
            }

            public async Task RegisterOwnershipStateAsync(string pendingStatePath)
            {
                if (!_registeredOwnershipStates.Any(registration =>
                        ScopeMatches(registration.Scope)
                        && registration.Path == pendingStatePath))
                {
                    _registeredOwnershipStates.Add(
                        new TransactionRegistration(_scope, pendingStatePath));
                    await PersistAsync().ConfigureAwait(false);
                }
            }

            public async Task UnregisterOwnershipStateAsync(string pendingStatePath)
            {
                if (_registeredOwnershipStates.RemoveAll(registration =>
                        ScopeMatches(registration.Scope)
                        && registration.Path == pendingStatePath) > 0)
                {
                    await PersistAsync().ConfigureAwait(false);
                }
            }

            public ValueTask DisposeAsync()
            {
                _stream.Dispose();
                return ValueTask.CompletedTask;
            }

            private bool ScopeMatches(string scope) =>
                string.Equals(
                    scope,
                    _scope,
                    StringComparison.Ordinal);

            private static FileStream CreateLockStream(string lockPath)
            {
                var options = new FileStreamOptions
                {
                    Mode = FileMode.OpenOrCreate,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.None,
                    BufferSize = 4096,
                    Options = FileOptions.Asynchronous | FileOptions.WriteThrough
                };
                if (!OperatingSystem.IsWindows())
                {
                    options.UnixCreateMode =
                        UnixFileMode.UserRead | UnixFileMode.UserWrite;
                }

                return new FileStream(lockPath, options);
            }

            private static TransactionState ReadTransactionState(
                FileStream stream,
                string currentScope)
            {
                stream.Position = 0;
                using var reader = new StreamReader(
                    stream,
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: true,
                    leaveOpen: true);
                var backups = new List<TransactionRegistration>();
                var staging = new List<TransactionRegistration>();
                var ownershipStates = new List<TransactionRegistration>();
                foreach (var line in reader.ReadToEnd()
                    .Split(
                        ['\r', '\n'],
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (line.StartsWith("S|", StringComparison.Ordinal))
                    {
                        staging.Add(DecodeRegistration(line[2..], currentScope));
                    }
                    else if (line.StartsWith("B|", StringComparison.Ordinal))
                    {
                        backups.Add(DecodeRegistration(line[2..], currentScope));
                    }
                    else if (line.StartsWith("O|", StringComparison.Ordinal))
                    {
                        ownershipStates.Add(
                            DecodeRegistration(line[2..], currentScope));
                    }
                    else
                    {
                        backups.Add(new TransactionRegistration(currentScope, line));
                    }
                }

                return new TransactionState(
                    backups.Distinct().ToList(),
                    staging.Distinct().ToList(),
                    ownershipStates.Distinct().ToList());
            }

            private async Task PersistAsync()
            {
                var contents = string.Join(
                    '\n',
                    _registeredBackups.Select(registration =>
                            $"B|{EncodeRegisteredPath(registration.Scope)}|{EncodeRegisteredPath(registration.Path)}")
                        .Concat(_registeredStaging.Select(registration =>
                            $"S|{EncodeRegisteredPath(registration.Scope)}|{EncodeRegisteredPath(registration.Path)}"))
                        .Concat(_registeredOwnershipStates.Select(registration =>
                            $"O|{EncodeRegisteredPath(registration.Scope)}|{EncodeRegisteredPath(registration.Path)}")));
                if (contents.Length > 0)
                {
                    contents += '\n';
                }

                var bytes = Encoding.UTF8.GetBytes(contents);
                _stream.Position = 0;
                _stream.SetLength(0);
                await _stream.WriteAsync(bytes).ConfigureAwait(false);
                await _stream.FlushAsync().ConfigureAwait(false);
                _stream.Flush(flushToDisk: true);
            }

            private static string EncodeRegisteredPath(string path) =>
                Convert.ToBase64String(Encoding.UTF8.GetBytes(path));

            private static TransactionRegistration DecodeRegistration(
                string value,
                string currentScope)
            {
                var separator = value.IndexOf('|');
                return separator < 0
                    ? new TransactionRegistration(
                        currentScope,
                        DecodeRegisteredPath(value))
                    : new TransactionRegistration(
                        DecodeRegisteredPath(value[..separator]),
                        DecodeRegisteredPath(value[(separator + 1)..]));
            }

            private static string DecodeRegisteredPath(string value) =>
                Encoding.UTF8.GetString(Convert.FromBase64String(value));

            private sealed record TransactionState(
                List<TransactionRegistration> Backups,
                List<TransactionRegistration> Staging,
                List<TransactionRegistration> OwnershipStates);

            private sealed record TransactionRegistration(
                string Scope,
                string Path);
        }

        private sealed record FileSystemMetadata(
            string SourcePath,
            string DestinationPath,
            bool IsDirectory,
            FileAttributes Attributes,
            DateTime CreationTimeUtc,
            DateTime LastAccessTimeUtc,
            DateTime LastWriteTimeUtc,
            UnixFileMode? UnixMode,
            byte[]? WindowsSecurityDescriptor)
        {
            public static FileSystemMetadata Capture(
                string sourcePath,
                string destinationPath,
                bool isDirectory) =>
                new(
                    sourcePath,
                    destinationPath,
                    isDirectory,
                    File.GetAttributes(sourcePath),
                    isDirectory
                        ? Directory.GetCreationTimeUtc(sourcePath)
                        : File.GetCreationTimeUtc(sourcePath),
                    isDirectory
                        ? Directory.GetLastAccessTimeUtc(sourcePath)
                        : File.GetLastAccessTimeUtc(sourcePath),
                    isDirectory
                        ? Directory.GetLastWriteTimeUtc(sourcePath)
                        : File.GetLastWriteTimeUtc(sourcePath),
                    OperatingSystem.IsWindows()
                        ? null
                        : File.GetUnixFileMode(sourcePath),
                    CaptureWindowsSecurityDescriptor(sourcePath, isDirectory));

            public void Apply(bool preserveTimestamps = true)
            {
                ApplyTo(
                    DestinationPath,
                    applyWindowsSecurity: true,
                    preserveTimestamps);
            }

            public void RestoreSource()
            {
                ApplyTo(
                    SourcePath,
                    applyWindowsSecurity: false,
                    preserveTimestamps: true);
            }

            private void ApplyTo(
                string path,
                bool applyWindowsSecurity,
                bool preserveTimestamps)
            {
                if (!OperatingSystem.IsWindows() && UnixMode is not null)
                {
                    File.SetUnixFileMode(path, UnixMode.Value);
                }

                if (preserveTimestamps && IsDirectory)
                {
                    Directory.SetCreationTimeUtc(path, CreationTimeUtc);
                    Directory.SetLastAccessTimeUtc(path, LastAccessTimeUtc);
                    Directory.SetLastWriteTimeUtc(path, LastWriteTimeUtc);
                }
                else if (preserveTimestamps)
                {
                    File.SetCreationTimeUtc(path, CreationTimeUtc);
                    File.SetLastAccessTimeUtc(path, LastAccessTimeUtc);
                    File.SetLastWriteTimeUtc(path, LastWriteTimeUtc);
                }

                File.SetAttributes(path, Attributes);
                if (applyWindowsSecurity && WindowsSecurityDescriptor is not null)
                {
                    ApplyWindowsSecurityDescriptor(
                        path,
                        IsDirectory,
                        WindowsSecurityDescriptor);
                }
            }

            public void RestoreSourceAccessTime()
            {
                if (IsDirectory)
                {
                    Directory.SetLastAccessTimeUtc(SourcePath, LastAccessTimeUtc);
                }
                else
                {
                    File.SetLastAccessTimeUtc(SourcePath, LastAccessTimeUtc);
                }
            }

            private static byte[]? CaptureWindowsSecurityDescriptor(
                string path,
                bool isDirectory)
            {
                if (!OperatingSystem.IsWindows())
                {
                    return null;
                }

                const AccessControlSections sections =
                    AccessControlSections.Access
                    | AccessControlSections.Owner
                    | AccessControlSections.Group;
                FileSystemSecurity security = isDirectory
                    ? new DirectoryInfo(path).GetAccessControl(sections)
                    : new FileInfo(path).GetAccessControl(sections);
                return security.GetSecurityDescriptorBinaryForm();
            }

            private static void ApplyWindowsSecurityDescriptor(
                string path,
                bool isDirectory,
                byte[] securityDescriptor)
            {
                if (!OperatingSystem.IsWindows())
                {
                    return;
                }

                if (isDirectory)
                {
                    var security = new DirectorySecurity();
                    security.SetSecurityDescriptorBinaryForm(
                        securityDescriptor,
                        AccessControlSections.Access);
                    new DirectoryInfo(path).SetAccessControl(security);
                }
                else
                {
                    var fileSecurity = new FileSecurity();
                    fileSecurity.SetSecurityDescriptorBinaryForm(
                        securityDescriptor,
                        AccessControlSections.Access);
                    new FileInfo(path).SetAccessControl(fileSecurity);
                }

                var expected = new RawSecurityDescriptor(securityDescriptor, 0);
                FileSystemSecurity current = isDirectory
                    ? new DirectoryInfo(path).GetAccessControl(
                        AccessControlSections.Owner | AccessControlSections.Group)
                    : new FileInfo(path).GetAccessControl(
                        AccessControlSections.Owner | AccessControlSections.Group);
                var changed = false;
                if (expected.Owner is not null
                    && !expected.Owner.Equals(current.GetOwner(typeof(SecurityIdentifier))))
                {
                    current.SetOwner(expected.Owner);
                    changed = true;
                }

                if (expected.Group is not null
                    && !expected.Group.Equals(current.GetGroup(typeof(SecurityIdentifier))))
                {
                    current.SetGroup(expected.Group);
                    changed = true;
                }

                if (!changed)
                {
                    return;
                }

                if (isDirectory)
                {
                    new DirectoryInfo(path).SetAccessControl((DirectorySecurity)current);
                }
                else
                {
                    new FileInfo(path).SetAccessControl((FileSecurity)current);
                }
            }
        }
    }

    private static string BuildGoogleAnalyticsSnippet(RenderContext configuration)
    {
        var measurementId = ResolveGoogleAnalyticsMeasurementId(configuration);
        if (string.IsNullOrWhiteSpace(measurementId))
        {
            return string.Empty;
        }

        var normalizedMeasurementId = measurementId.Trim();
        if (!Regex.IsMatch(normalizedMeasurementId, "^G-[A-Za-z0-9]+$", RegexOptions.CultureInvariant))
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.AppendLine($"<script async src=\"https://www.googletagmanager.com/gtag/js?id={normalizedMeasurementId}\"></script>");
        builder.AppendLine("<script>");
        builder.AppendLine("  window.dataLayer = window.dataLayer || [];");
        builder.AppendLine("  function gtag(){dataLayer.push(arguments);}");
        builder.AppendLine("  gtag('js', new Date());");
        builder.AppendLine($"  gtag('config', {JsonSerializer.Serialize(normalizedMeasurementId)});");
        builder.Append("</script>");
        return builder.ToString();
    }

    private static string ResolveGoogleAnalyticsMeasurementId(RenderContext configuration)
    {
        if (!string.IsNullOrWhiteSpace(configuration.Site.GoogleAnalyticsMeasurementId))
        {
            return configuration.Site.GoogleAnalyticsMeasurementId.Trim();
        }

        var fromEnvironment = Environment.GetEnvironmentVariable("GA_MEASUREMENT_ID");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment.Trim();
        }

        fromEnvironment = Environment.GetEnvironmentVariable("GOOGLE_ANALYTICS_MEASUREMENT_ID");
        return string.IsNullOrWhiteSpace(fromEnvironment) ? string.Empty : fromEnvironment.Trim();
    }

    private static string BuildFaviconLinks(RenderContext configuration)
    {
        if (!configuration.HasFaviconAssets)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.AppendLine($"              <link rel=\"icon\" type=\"image/x-icon\" sizes=\"any\" href=\"{Html.Encode(configuration.Routes.PublicPath(configuration.Routes.Favicon("favicon.ico")))}\">");
        foreach (var (fileName, sizes) in PngFaviconAssets)
        {
            builder.AppendLine($"              <link rel=\"icon\" type=\"image/png\" sizes=\"{sizes}\" href=\"{Html.Encode(configuration.Routes.PublicPath(configuration.Routes.Favicon(fileName)))}\">");
        }

        builder.AppendLine($"              <link rel=\"apple-touch-icon\" sizes=\"180x180\" href=\"{Html.Encode(configuration.Routes.PublicPath(configuration.Routes.Favicon("apple-touch-icon.png")))}\">");
        builder.Append($"              <link rel=\"manifest\" href=\"{Html.Encode(configuration.Routes.PublicPath(configuration.Routes.WebManifest))}\">");
        return builder.ToString();
    }

    private static string BuildHomeIconSvg() => """
        <svg aria-hidden="true" viewBox="0 0 24 24">
          <path d="M4.5 10.5L12 4.5l7.5 6v8.25a.75.75 0 0 1-.75.75h-4.5a.75.75 0 0 1-.75-.75V15a1.5 1.5 0 0 0-3 0v3.75a.75.75 0 0 1-.75.75h-4.5a.75.75 0 0 1-.75-.75z" />
        </svg>
        """;

    private static string BuildMenuIconSvg() => """
        <svg aria-hidden="true" viewBox="0 0 24 24">
          <path d="M4.5 7.5h15M4.5 12h15M4.5 16.5h15" />
        </svg>
        """;

    private static string BuildWebManifest(RenderContext configuration)
    {
        var manifest = new
        {
            name = configuration.Site.Title,
            short_name = configuration.Site.Title,
            description = configuration.Site.Description,
            start_url = configuration.Routes.PublicPath(configuration.Routes.Home),
            scope = configuration.Routes.PublicPath(configuration.Routes.Root),
            display = "standalone",
            background_color = configuration.Theme.ThemeColor,
            theme_color = configuration.Theme.ThemeColor,
            icons = ManifestIconAssets.Select(asset => new
            {
                src = configuration.Routes.PublicPath(configuration.Routes.Favicon(asset.FileName)),
                sizes = asset.Sizes,
                type = "image/png"
            })
        };

        return JsonSerializer.Serialize(manifest, WebManifestSerializerOptions);
    }

    private static string BuildDocsCss(RenderContext configuration) =>
        BuildCss(configuration) + """

        @layer layout {
          .docs-header,
          .docs-shell,
          .docs-footer {
            inline-size: min(100%, 90rem);
            margin-inline: auto;
          }
          .docs-header {
            min-block-size: 4.25rem;
            padding-inline: 1.25rem;
            display: flex;
            justify-content: space-between;
            align-items: center;
            position: sticky;
            inset-block-start: 0;
            z-index: 20;
            border-block-end: 1px solid var(--border);
            background: rgba(13, 17, 23, 0.94);
            backdrop-filter: blur(0.8rem);
          }
          .docs-shell {
            display: grid;
            grid-template-columns: minmax(13rem, 16rem) minmax(0, 1fr) minmax(12rem, 15rem);
            gap: clamp(1.25rem, 3vw, 3rem);
            align-items: start;
            padding: 2rem 1.25rem 4rem;
          }
          .docs-main {
            min-inline-size: 0;
          }
          .docs-sidebar {
            position: sticky;
            inset-block-start: 5.75rem;
            max-block-size: calc(100svh - 7rem);
            overflow-y: auto;
            padding-inline-end: 0.75rem;
          }
          .docs-footer {
            padding: 1.5rem 1.25rem 3rem;
            color: var(--muted);
            border-block-start: 1px solid var(--border);
          }
        }

        @layer components {
          .docs-brand {
            color: #f0f6fc;
            font-size: 1.15rem;
            font-weight: 750;
            text-decoration: none;
          }
          .docs-brand::before {
            content: "◆ ";
            color: var(--accent);
          }
          .docs-menu-toggle {
            display: none;
            padding: 0.45rem 0.8rem;
            color: #f0f6fc;
            background: var(--panel);
            border: 1px solid var(--border);
            border-radius: 0.45rem;
            cursor: pointer;
          }
          .docs-nav-list,
          .docs-nav-list ul {
            display: grid;
            gap: 0.12rem;
          }
          .docs-nav-list ul {
            margin: 0.25rem 0 0.5rem 0.7rem;
            padding-inline-start: 0.7rem;
            border-inline-start: 1px solid var(--border);
          }
          .docs-nav-folder > span {
            display: block;
            margin: 0.7rem 0 0.25rem;
            color: var(--text);
            font-size: 0.78rem;
            font-weight: 700;
            letter-spacing: 0.04em;
            text-transform: uppercase;
          }
          .docs-nav-folder.is-ancestor > span {
            color: var(--accent);
          }
          .docs-nav-link {
            display: block;
            padding: 0.34rem 0.55rem;
            color: var(--muted);
            border-radius: 0.35rem;
            text-decoration: none;
          }
          .docs-nav-link:hover,
          .docs-nav-link:focus-visible {
            color: #f0f6fc;
            background: rgba(88, 166, 255, 0.13);
          }
          .docs-nav-link.is-current {
            color: #f0f6fc;
            background: rgba(88, 166, 255, 0.2);
            box-shadow: inset 0.2rem 0 var(--accent);
          }
          .docs-content {
            inline-size: min(100%, 48rem);
            margin-inline: auto;
          }
          .docs-content h1 {
            margin-block: 0 1rem;
            font-size: clamp(2rem, 5vw, 3.3rem);
          }
          .docs-content h2 {
            margin-block: 2.5rem 0.8rem;
            padding-block-end: 0.45rem;
            border-block-end: 1px solid var(--border);
          }
          .docs-content h3 {
            margin-block: 1.8rem 0.6rem;
          }
          .docs-content :where(p, ul, ol, blockquote, pre) {
            margin-block: 1rem;
          }
          .docs-content :where(ul, ol) {
            padding-inline-start: 1.5rem;
          }
          .docs-content ul {
            list-style: disc;
          }
          .docs-content ol {
            list-style: decimal;
          }
          .docs-content li + li {
            margin-block-start: 0.35rem;
          }
          .docs-content blockquote {
            padding-inline-start: 1rem;
            color: var(--muted);
            border-inline-start: 0.25rem solid var(--accent);
          }
          .docs-description {
            color: var(--muted);
            font-size: 1.1rem;
          }
          .docs-hero {
            padding: clamp(2rem, 7vw, 5rem);
            border: 1px solid var(--border);
            border-radius: var(--radius);
            background:
              linear-gradient(135deg, rgba(88, 166, 255, 0.22), transparent 52%),
              var(--panel);
          }
          .docs-hero h1 {
            margin-block: 0.5rem 1rem;
          }
          .docs-kicker {
            color: var(--accent);
            font-weight: 700;
            letter-spacing: 0.08em;
            text-transform: uppercase;
          }
          .docs-primary-link {
            display: inline-block;
            margin-block-start: 1rem;
            padding: 0.65rem 1rem;
            color: var(--accent-contrast);
            background: var(--accent);
            border-radius: 0.45rem;
            font-weight: 700;
            text-decoration: none;
          }
          .docs-toc {
            margin: 0;
            padding: 0;
            border: 0;
            border-radius: 0;
            background: transparent;
            box-shadow: none;
          }
          .docs-toc.post-toc {
            inset-block-start: 5.75rem;
          }
          .docs-pagination {
            display: grid;
            grid-template-columns: repeat(2, minmax(0, 1fr));
            gap: 1rem;
            margin-block-start: 3rem;
            padding-block-start: 1.5rem;
            border-block-start: 1px solid var(--border);
          }
          .docs-pagination a {
            display: grid;
            gap: 0.25rem;
            padding: 0.85rem;
            color: #f0f6fc;
            border: 1px solid var(--border);
            border-radius: 0.5rem;
            text-decoration: none;
          }
          .docs-pagination a:hover,
          .docs-pagination a:focus-visible {
            border-color: var(--accent);
            background: rgba(88, 166, 255, 0.1);
          }
          .docs-pagination-next {
            text-align: end;
          }
          .docs-pagination small {
            color: var(--muted);
          }
        }

        @media (max-width: 70rem) {
          .docs-shell {
            grid-template-columns: minmax(0, 1fr) minmax(12rem, 15rem);
          }
          .docs-sidebar {
            position: fixed;
            inset: 4.25rem auto 0 0;
            z-index: 15;
            inline-size: min(19rem, 85vw);
            max-block-size: none;
            padding: 1rem;
            border-inline-end: 1px solid var(--border);
            background: var(--surface);
            box-shadow: var(--shadow);
            transform: translateX(-105%);
            transition: transform 0.2s ease;
          }
          .docs-sidebar.docs-sidebar-open {
            transform: translateX(0);
          }
          .docs-menu-toggle {
            display: inline-flex;
          }
        }

        @media (max-width: 52rem) {
          .docs-shell {
            grid-template-columns: 1fr;
            padding-block-start: 1.25rem;
          }
          .docs-toc.post-toc {
            position: static;
            grid-row: 2;
            max-block-size: none;
            padding-block-start: 1.25rem;
            border-block-start: 1px solid var(--border);
          }
        }
        """;

    private static string BuildCss(RenderContext configuration)
    {
        var css = BuildBaseCss().Replace("__LITHOSHARP_BRAND_PREFIX__", configuration.Theme.BrandPrefix, StringComparison.Ordinal);
        var additional = configuration.Theme.AdditionalCss;
        return string.IsNullOrEmpty(additional) ? css : css + "\n\n" + additional;
    }

    private static string BuildDocsScript() =>
        BuildSiteScript() + """

        (() => {
          "use strict";

          const toggle = document.querySelector("[data-docs-menu-toggle]");
          const sidebar = document.querySelector("[data-docs-sidebar]");
          if (!toggle || !sidebar) {
            return;
          }

          const setOpen = (isOpen) => {
            toggle.setAttribute("aria-expanded", String(isOpen));
            sidebar.classList.toggle("docs-sidebar-open", isOpen);
          };

          toggle.addEventListener("click", () => {
            setOpen(toggle.getAttribute("aria-expanded") !== "true");
          });

          for (const link of sidebar.querySelectorAll("a")) {
            link.addEventListener("click", () => setOpen(false));
          }

          document.addEventListener("keydown", (event) => {
            if (event.key === "Escape") {
              setOpen(false);
              toggle.focus();
            }
          });

          window.addEventListener("resize", () => setOpen(false));
          setOpen(false);
        })();
        """;

    private static string BuildBaseCss() => """
        @layer reset, base, layout, components, utilities;

        @view-transition { navigation: auto; }

        :root {
          color-scheme: dark;
          --surface: #0d1117;
          --panel: #161b22;
          --panel-strong: #21262d;
          --text: #c9d1d9;
          --muted: #8b949e;
          --accent: #58a6ff;
          --accent-contrast: #0d1117;
          --success: #3fb950;
          --attention: #d29922;
          --danger: #f85149;
          --done: #bc8cff;
          --border: #30363d;
          --radius: 1.1rem;
          --shadow: 0 1rem 3rem rgba(1, 4, 9, 0.65);
          --measure: min(72rem, 100% - 2.5rem);
          --space: clamp(1rem, 0.6rem + 1.6vw, 1.75rem);
          scrollbar-color: var(--accent) transparent;
          accent-color: var(--accent);
        }

        @layer reset {
          *, *::before, *::after { box-sizing: border-box; }
          body, h1, h2, h3, h4, p, figure, ul { margin: 0; }
          ul { padding: 0; list-style: none; }
          :focus-visible { outline: 2px solid var(--accent); outline-offset: 3px; border-radius: 0.35rem; }
          img { max-inline-size: 100%; block-size: auto; }
          button, input { font: inherit; }
        }

        @layer base {
          body {
            font-family: Consolas, "Cascadia Code", "Yu Gothic UI", "Meiryo", monospace;
            background:
              radial-gradient(80rem 45rem at 100% -10%, rgba(88, 166, 255, 0.2), transparent 58%),
              radial-gradient(65rem 38rem at 0% 0%, rgba(188, 140, 255, 0.16), transparent 56%),
              radial-gradient(55rem 32rem at 50% 120%, rgba(63, 185, 80, 0.1), transparent 62%),
              var(--surface);
            background-attachment: fixed;
            color: var(--text);
            line-height: 1.7;
            text-wrap: pretty;
            -webkit-text-size-adjust: 100%;
            min-block-size: 100svb;
            display: flex;
            flex-direction: column;
          }
          a { color: var(--accent); text-underline-offset: 0.2em; }
          a:hover:not(:where(.brand, .card a)) { text-decoration-thickness: 2px; }
          h1, h2, h3 { text-wrap: balance; line-height: 1.15; }
          :where(h1, h2, h3) { letter-spacing: -0.01em; }
          h1 { color: #f0f6fc; }
          h2 { color: var(--done); }
          h3 { color: var(--success); }
          h4 { color: var(--attention); }
          pre, code { font-family: Consolas, "Cascadia Code", monospace; }
          pre, code { background: var(--panel-strong); border-radius: 0.5rem; color: #f0f6fc; }
          code { padding-inline: 0.3em; padding-block: 0.1em; }
          pre { overflow-x: auto; padding: 1rem; border: 1px solid var(--border); }
          pre code { background: none; padding: 0; }
        }

        @layer layout {
          .site-header, main, .site-footer { inline-size: var(--measure); margin-inline: auto; }
          main { flex: 1; }
          .site-header {
            display: flex;
            justify-content: space-between;
            align-items: center;
            gap: 1rem;
            flex-wrap: wrap;
            padding-block: 1.25rem;
            position: sticky;
            inset-block-start: 0;
            z-index: 1;
            backdrop-filter: blur(0.5rem);
          }
          .site-nav-shell {
            display: grid;
            grid-template-columns: auto minmax(0, 1fr) auto;
            align-items: center;
            gap: 0.75rem;
            flex: 1 1 auto;
            min-inline-size: 0;
            margin-inline-start: auto;
            position: relative;
          }
          .site-nav {
            position: absolute;
            inset-block-start: calc(100% + 0.65rem);
            inset-inline-end: 0;
            display: none;
            flex-direction: column;
            gap: 0.35rem;
            min-inline-size: min(18rem, calc(100vw - 2rem));
            padding: 0.5rem;
            border: 1px solid var(--border);
            border-radius: 1rem;
            background: rgba(22, 27, 34, 0.96);
            box-shadow: var(--shadow);
          }
          .site-nav.site-nav-open { display: flex; }
          .site-footer { padding-block: 3rem 4rem; color: var(--muted); }
          section { margin-block: clamp(2rem, 5vw, 3.5rem); }
        }

        @layer components {
          .brand { font-weight: 700; font-size: 1.15rem; color: var(--text); text-decoration: none; }
          .brand::before { content: "__LITHOSHARP_BRAND_PREFIX__"; color: var(--accent); }
          .site-home-link,
          .site-menu-toggle { display: inline-flex; }
          .site-nav a {
            color: var(--muted);
            text-decoration: none;
            padding: 0.5rem 0.85rem;
            border-radius: 999px;
            min-block-size: 2.75rem;
            display: inline-flex;
            align-items: center;
            inline-size: 100%;
            justify-content: flex-start;
            transition: background-color 0.2s, color 0.2s;
          }
          .site-nav a:hover,
          .site-nav a:focus-visible { color: #f0f6fc; background: rgba(88, 166, 255, 0.14); }
          .site-nav-home { display: none; }
          .site-icon-button {
            inline-size: 2.75rem;
            block-size: 2.75rem;
            justify-content: center;
            align-items: center;
            padding: 0;
            color: #f0f6fc;
            border: 1px solid rgba(88, 166, 255, 0.34);
            background: rgba(88, 166, 255, 0.1);
            border-radius: 999px;
            text-decoration: none;
            display: inline-flex;
            appearance: none;
            cursor: pointer;
            transition: background-color 0.2s, color 0.2s, border-color 0.2s;
          }
          .site-icon-button:hover,
          .site-icon-button:focus-visible {
            color: #f0f6fc;
            background: rgba(88, 166, 255, 0.2);
            border-color: rgba(88, 166, 255, 0.5);
          }
          .site-icon-button svg {
            inline-size: 1.25rem;
            block-size: 1.25rem;
            fill: none;
            stroke: currentColor;
            stroke-width: 1.8;
            stroke-linecap: round;
            stroke-linejoin: round;
          }

          .visually-hidden {
            position: absolute;
            inline-size: 1px;
            block-size: 1px;
            overflow: hidden;
            clip: rect(0 0 0 0);
            white-space: nowrap;
            clip-path: inset(50%);
          }

          .surface, .hero, .card, .source, .post, .post-toc, .archive-panel {
            background: rgba(22, 27, 34, 0.88);
            border: 1px solid var(--border);
            border-radius: var(--radius);
            padding: var(--space);
            box-shadow: var(--shadow);
          }

          .hero {
            padding: clamp(1.75rem, 4vw, 3rem);
            background:
              linear-gradient(135deg, rgba(88, 166, 255, 0.18), transparent 42%),
              linear-gradient(225deg, rgba(188, 140, 255, 0.16), transparent 52%),
              rgba(22, 27, 34, 0.92);
          }
          .hero h1 { font-size: clamp(2.25rem, 6vw, 4.25rem); line-height: 1.05; margin-block: 0.4rem 0.75rem; }
          .eyebrow { color: var(--success); font-weight: 600; letter-spacing: 0.04em; text-transform: uppercase; font-size: 0.8rem; }

          .browser-search {
            display: grid;
            grid-template-columns: minmax(0, 1fr) auto;
            align-items: center;
            gap: 0.5rem;
            min-block-size: 3rem;
            padding: 0.35rem 0.4rem 0.35rem 0.8rem;
            border: 1px solid rgba(88, 166, 255, 0.38);
            border-radius: 999px;
            background:
              linear-gradient(180deg, rgba(33, 38, 45, 0.96), rgba(13, 17, 23, 0.92)),
              rgba(13, 17, 23, 0.92);
            box-shadow: inset 0 1px 0 rgba(240, 246, 252, 0.08), 0 1rem 2.5rem rgba(1, 4, 9, 0.32);
          }
          .browser-search input {
            inline-size: 100%;
            min-inline-size: 0;
            border: 0;
            color: #f0f6fc;
            background: transparent;
            outline: 0;
          }
          .browser-search input::placeholder { color: var(--muted); }
          .browser-search button {
            border: 0;
            padding: 0.2rem;
            color: var(--accent);
            background: transparent;
            text-decoration: none;
            line-height: 1;
            cursor: pointer;
            display: inline-flex;
            justify-content: center;
            align-items: center;
            transition: color 0.2s;
          }
          .browser-search:focus-within {
            border-color: var(--accent);
            box-shadow: 0 0 0 3px rgba(88, 166, 255, 0.25), inset 0 1px 0 rgba(240, 246, 252, 0.08);
          }
          .browser-search button:hover,
          .browser-search button:focus-visible {
            color: #f0f6fc;
          }
          .browser-search button svg {
            inline-size: 1.1rem;
            block-size: 1.1rem;
            fill: none;
            stroke: currentColor;
            stroke-width: 1.8;
            stroke-linecap: round;
            stroke-linejoin: round;
          }
          .site-header-search {
            display: grid;
            inline-size: 100%;
            min-inline-size: 0;
            margin: 0;
          }

          .post-list, .source-grid {
            display: grid;
            grid-template-columns: repeat(auto-fill, minmax(min(100%, 17rem), 1fr));
            gap: 1rem;
            container-type: inline-size;
          }
          .featured-post-list {
            display: grid;
            grid-template-columns: minmax(0, 1fr);
          }
          .tag-cloud {
            display: flex;
            flex-wrap: wrap;
            gap: 0.75rem;
          }
          .tag-link {
            display: inline-flex;
            align-items: center;
            gap: 0.6rem;
            padding: 0.7rem 0.95rem;
            border: 1px solid var(--border);
            border-radius: 999px;
            background: rgba(22, 27, 34, 0.82);
            color: #f0f6fc;
            text-decoration: none;
            transition: border-color 0.2s ease, transform 0.2s ease, background-color 0.2s ease;
          }
          .tag-link span {
            color: var(--done);
            font-weight: 700;
          }
          .tag-link small { color: var(--muted); }
          .tag-link:hover,
          .tag-link:focus-visible {
            border-color: var(--accent);
            background: rgba(22, 27, 34, 0.96);
            transform: translateY(-1px);
          }
          .archive-post-list {
            display: grid;
            gap: 0.65rem;
          }

          .card {
            display: flex;
            flex-direction: column;
            gap: 0.4rem;
            transition: transform 0.2s ease, box-shadow 0.2s ease, border-color 0.2s ease;
          }
          .card:has(a:hover), .card:has(a:focus-visible) { transform: translateY(-3px); border-color: var(--accent); }
          .card h3 { margin: 0; font-size: 1.2rem; }
          .card a { color: #f0f6fc; text-decoration: none; }
          .card a::after { content: ""; position: absolute; inset: 0; }
          .card { position: relative; }
          .card time, .card p, .source small { color: var(--muted); }
          .card time { font-variant-numeric: tabular-nums; font-size: 0.85rem; }
          .featured-card {
            display: grid;
            grid-template-columns: minmax(16rem, 22rem) minmax(0, 1fr);
            grid-template-areas:
              "time summary"
              "title summary";
            gap: 0.75rem 1.75rem;
            align-items: start;
            padding: clamp(1.35rem, 3vw, 2rem);
            background:
              linear-gradient(135deg, rgba(88, 166, 255, 0.14), transparent 40%),
              linear-gradient(225deg, rgba(188, 140, 255, 0.12), transparent 58%),
              rgba(22, 27, 34, 0.9);
          }
          .featured-card time { grid-area: time; }
          .featured-card h3 { grid-area: title; font-size: clamp(1.5rem, 3.5vw, 2.3rem); }
          .featured-card p {
            grid-area: summary;
            margin: 0;
            font-size: 1rem;
            line-height: 1.75;
          }
          .archive-row {
            display: grid;
            grid-template-columns: minmax(12rem, 15rem) minmax(7rem, 10rem) minmax(0, 1fr);
            align-items: center;
            gap: 1rem;
            padding: 0.9rem 1rem;
            border: 1px solid var(--border);
            border-radius: 1rem;
            background: rgba(22, 27, 34, 0.72);
            transition: border-color 0.2s ease, background-color 0.2s ease, transform 0.2s ease;
          }
          .archive-row:hover,
          .archive-row:focus-within {
            border-color: rgba(88, 166, 255, 0.42);
            background: rgba(22, 27, 34, 0.88);
            transform: translateY(-1px);
          }
          .archive-row time,
          .archive-row p {
            color: var(--muted);
            margin: 0;
            font-size: 0.92rem;
          }
          .archive-row time {
            font-variant-numeric: tabular-nums;
            white-space: nowrap;
          }
          .archive-row h3 {
            margin: 0;
            min-inline-size: 0;
            font-size: 1rem;
          }
          .archive-row a {
            color: #f0f6fc;
            text-decoration: none;
          }
          .archive-row a:hover,
          .archive-row a:focus-visible {
            color: var(--accent);
          }
          .archive-row h3,
          .archive-row p {
            overflow: hidden;
            text-overflow: ellipsis;
            white-space: nowrap;
          }

          .source { display: grid; gap: 0.25rem; color: var(--text); text-decoration: none; }
          .source span { color: var(--success); font-weight: 600; }

          .archive-panels {
            display: grid;
            gap: 1rem;
          }
          .archive-panel {
            overflow: clip;
          }
          .archive-panel summary {
            display: flex;
            justify-content: space-between;
            align-items: center;
            gap: 1rem;
            cursor: pointer;
            color: var(--done);
            font-weight: 700;
            list-style: none;
          }
          .archive-panel summary::-webkit-details-marker { display: none; }
          .archive-panel summary::before {
            content: "▶";
            color: var(--accent);
            transition: transform 0.2s ease;
          }
          .archive-panel[open] summary::before { transform: rotate(90deg); }
          .archive-panel summary span { flex: 1; }
          .archive-panel summary small { color: var(--muted); font-weight: 400; }
          .archive-panel .archive-post-list {
            margin-block-start: 1rem;
          }

          .post-layout {
            display: grid;
            grid-template-columns: minmax(0, 7fr) minmax(16rem, 3fr);
            gap: clamp(1rem, 3vw, 2rem);
            align-items: start;
            margin-block: 2rem;
          }
          .post { margin-block: 0; }
          .post-title { font-size: clamp(1.9rem, 4.5vw, 3.25rem); }
          .post .summary { color: var(--muted); font-size: 1.1rem; }
          .post :where(h2, h3) { scroll-margin-top: 6.5rem; }
          .post :where(h2, h3) { margin-block-start: 2rem; }
          .post h4 {
            margin-block: 1.35rem 0.35rem;
            font-size: 1rem;
            line-height: 1.45;
          }
          .post h4 a {
            color: var(--accent);
            text-decoration: none;
            border-block-end: 1px solid rgba(88, 166, 255, 0.45);
          }
          .post h4 a:hover,
          .post h4 a:focus-visible {
            color: #79c0ff;
            border-block-end-color: currentColor;
          }
          .post :where(p, ul, ol) { margin-block: 1rem; }
          .post a { word-break: break-word; }
          .post-toc {
            position: sticky;
            inset-block-start: 5.5rem;
            max-block-size: calc(100svh - 7rem);
            overflow: auto;
          }
          .post-toc h2 {
            margin: 0 0 1rem;
            color: #f0f6fc;
            font-size: 1.2rem;
          }
          .toc-nav {
            position: relative;
          }
          .toc-track,
          .toc-indicator {
            position: absolute;
            inset-inline-start: calc(0.75rem - 1px);
            inline-size: 2px;
            border-radius: 999px;
            pointer-events: none;
          }
          .toc-track {
            inset-block: 0.85rem 0.85rem;
            background: rgba(88, 166, 255, 0.2);
          }
          .toc-indicator {
            inset-block-start: 0.85rem;
            block-size: 2.2rem;
            background: linear-gradient(180deg, #58a6ff, #1f6feb);
            box-shadow: 0 0 0 1px rgba(88, 166, 255, 0.16), 0 0 16px rgba(31, 111, 235, 0.3);
            transition: transform 0.2s ease, block-size 0.2s ease, opacity 0.2s ease;
            opacity: 0;
          }
          .toc-list {
            display: grid;
            gap: 0.55rem;
            margin: 0;
            padding: 0 0 0 1.75rem;
            list-style: none;
          }
          .toc-list li {
            position: relative;
          }
          .toc-list li::before {
            content: "";
            position: absolute;
            inset-inline-start: -1.25rem;
            inset-block-start: 0.6rem;
            inline-size: 0.5rem;
            block-size: 0.5rem;
            border-radius: 50%;
            background: rgba(88, 166, 255, 0.8);
            box-shadow: 0 0 0 0.1rem rgba(31, 111, 235, 0.16);
            transition: transform 0.2s ease, background-color 0.2s ease, box-shadow 0.2s ease;
          }
          .post-toc a {
            display: block;
            color: var(--muted);
            text-decoration: none;
            padding-inline-start: 0.35rem;
            transition: color 0.2s ease, transform 0.2s ease;
          }
          .toc-depth-1 a {
            color: var(--accent);
            font-weight: 700;
          }
          .toc-depth-2 a {
            color: var(--text);
          }
          .toc-link-active {
            color: #f0f6fc !important;
            transform: translateX(0.15rem);
          }
          .toc-list li.toc-item-active::before {
            background: #58a6ff;
            box-shadow: 0 0 0 0.16rem rgba(31, 111, 235, 0.3), 0 0 16px rgba(88, 166, 255, 0.3);
            transform: scale(1.15);
          }
          .post-toc a:hover,
          .post-toc a:focus-visible {
            color: #f0f6fc;
            transform: translateX(0.15rem);
          }
          .toc-depth-2 { padding-inline-start: 0.9rem; }
          .toc-depth-3 { padding-inline-start: 1.8rem; }

          .tags { display: flex; gap: 0.5rem; flex-wrap: wrap; margin-block: 1rem; }
          .tags span {
            border: 1px solid var(--border);
            border-radius: 999px;
            padding: 0.25rem 0.7rem;
            color: var(--done);
            background: rgba(188, 140, 255, 0.1);
            font-size: 0.85rem;
          }

          .search-box { margin-block-start: 1.25rem; }
          #search-input {
            inline-size: 100%;
            font: inherit;
            font-size: 1.1rem;
            padding: 0.85rem 1.1rem;
            min-block-size: 3rem;
            color: var(--text);
            background: rgba(22, 27, 34, 0.9);
            border: 1px solid var(--border);
            border-radius: 999px;
            transition: border-color 0.2s, box-shadow 0.2s;
          }
          #search-input:focus-visible {
            outline: none;
            border-color: var(--accent);
            box-shadow: 0 0 0 3px rgba(88, 166, 255, 0.3);
          }
          .search-scope {
            color: var(--done);
            font-size: 0.92rem;
            margin-block: 1rem 0 0.35rem;
          }
          .search-status { color: var(--muted); font-size: 0.9rem; margin-block: 1rem; }
          #search-app { margin-block-start: 1.5rem; }
          .release-note-list {
            display: grid;
            gap: 1.25rem;
          }
          .release-note {
            display: grid;
            gap: 1rem;
          }
          .release-note-header {
            display: grid;
            gap: 0.45rem;
          }
          .release-note-meta {
            color: var(--muted);
            font-size: 0.9rem;
          }
          .card mark {
            background: rgba(210, 153, 34, 0.35);
            color: #f0f6fc;
            border-radius: 0.2rem;
            padding-inline: 0.1em;
          }
          .search-empty { color: var(--muted); }
        }

        @media (min-width: 34rem) {
          .hero { padding-inline: clamp(2rem, 5vw, 3.5rem); }
        }

        @media (max-width: 40rem) {
          .site-header {
            padding-block: 0.9rem;
          }
          .site-header .brand {
            display: none;
          }
          .site-nav-shell {
            inline-size: 100%;
            gap: 0.65rem;
          }
          .site-home-link,
          .site-menu-toggle,
          .site-header-search {
            display: inline-flex;
          }
          .site-header-search {
            display: grid;
            grid-template-columns: minmax(0, 1fr) auto;
            min-block-size: 2.75rem;
            padding: 0.25rem 0.35rem 0.25rem 0.8rem;
            gap: 0.35rem;
          }
          .site-header-search input {
            font-size: 0.95rem;
          }
          .site-header-search button {
            padding: 0;
          }
          .site-menu-toggle {
            justify-self: end;
          }
          .site-nav {
            inline-size: 100%;
            grid-column: 1 / -1;
            min-inline-size: 0;
            margin-block-start: 0.65rem;
            position: static;
            inset-block-start: auto;
            inset-inline-end: auto;
          }
          .site-nav.site-nav-open {
            display: flex;
          }
          .site-nav a { font-size: 0.95rem; }
        }

        @media (max-width: 54rem) {
          .archive-row {
            grid-template-columns: 1fr;
            gap: 0.35rem;
            align-items: start;
          }
          .archive-row h3,
          .archive-row p {
            white-space: normal;
          }
          .featured-card {
            grid-template-columns: 1fr;
            grid-template-areas:
              "time"
              "title"
              "summary";
          }
          .post-layout {
            grid-template-columns: 1fr;
          }
          .post-toc {
            position: static;
            max-block-size: none;
            order: -1;
          }
        }

        @media (prefers-reduced-motion: reduce) {
          @view-transition { navigation: none; }
          *, *::before, *::after { animation-duration: 0.01ms !important; transition-duration: 0.01ms !important; }
        }
        """;

    private static string BuildSearchScript(SiteText text)
    {
        var messages = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["browseAll"] = text.SearchStatusBrowseAll,
            ["hits"] = text.SearchStatusHits,
            ["hitsInTag"] = text.SearchStatusHitsInTag,
            ["showingTopSuffix"] = text.SearchStatusShowingTopSuffix,
            ["taggedPosts"] = text.SearchStatusTaggedPosts,
            ["noMatch"] = text.SearchStatusNoMatch,
            ["noMatchInTag"] = text.SearchStatusNoMatchInTag,
            ["noPostsInTag"] = text.SearchStatusNoPostsInTag,
            ["noPosts"] = text.SearchStatusNoPosts,
            ["scopeTag"] = text.SearchScopeTag,
            ["loadError"] = text.SearchStatusLoadError,
        };
        return SearchScriptTemplate.Replace("__LITHOSHARP_SEARCH_MESSAGES__", SerializeSearchMessages(messages), StringComparison.Ordinal);
    }

    private static string SerializeSearchMessages(Dictionary<string, string> messages)
    {
        var builder = new StringBuilder();
        builder.Append('{');
        var first = true;
        foreach (var (key, value) in messages)
        {
            if (!first)
            {
                builder.Append(',');
            }

            first = false;
            builder.Append('"').Append(key).Append("\":").Append(JsonSerializer.Serialize(value, SearchMessageSerializerOptions));
        }

        builder.Append('}');
        return builder.ToString();
    }

    private const string SearchScriptTemplate = """
        (() => {
          "use strict";
          const app = document.getElementById("search-app");
          const input = document.getElementById("search-input");
          const results = document.getElementById("search-results");
          const status = document.getElementById("search-status");
          const scope = document.getElementById("search-scope");
          if (!app || !input || !results || !status) {
            return;
          }

          const indexUrl = app.dataset.index;
          let docs = null;
          const RESULT_LIMIT = 30;
          const RECENT_LIMIT = 20;
          const pageParams = new URLSearchParams(window.location.search);
          const selectedTag = (pageParams.get("tag") || "").trim();
          const MESSAGES = __LITHOSHARP_SEARCH_MESSAGES__;
          const fmt = (template, values) =>
            Object.keys(values).reduce((acc, key) => acc.split("{" + key + "}").join(String(values[key])), template);

          const escapeHtml = (value) =>
            value.replace(/[&<>"']/g, (ch) => ({
              "&": "&amp;",
              "<": "&lt;",
              ">": "&gt;",
              '"': "&quot;",
              "'": "&#39;",
            })[ch]);

          const normalizeText = (value) =>
            String(value || "")
              .toLowerCase()
              .normalize("NFKC")
              .replace(/[!"#$%&'()*+,./:;<=>?@[\\\]^_`{|}~-]+/g, " ")
              .replace(/\s+/g, " ")
              .trim();

          const normalizedSelectedTag = normalizeText(selectedTag);

          // Prefer substring matches; fall back to fuzzy subsequence scoring.
          const scoreField = (needle, hay) => {
            const idx = hay.indexOf(needle);
            if (idx !== -1) {
              let score = 1000 - Math.min(idx, 500);
              if (idx === 0 || /\s/.test(hay[idx - 1])) {
                score += 250;
              }
              return score;
            }
            let cursor = 0;
            let streak = 0;
            let score = 0;
            for (let i = 0; i < needle.length; i += 1) {
              const ch = needle[i];
              let found = -1;
              for (; cursor < hay.length; cursor += 1) {
                if (hay[cursor] === ch) {
                  found = cursor;
                  break;
                }
              }
              if (found === -1) {
                return -1;
              }
              streak = found > 0 && /\s/.test(hay[found - 1]) ? streak + 3 : streak + 1;
              score += streak;
              cursor = found + 1;
            }
            return score;
          };

          const scoreDoc = (tokens, doc) => {
            let total = 0;
            for (const token of tokens) {
              const hayScore = scoreField(token, doc.hay);
              if (hayScore < 0) {
                return -1;
              }
              const titleScore = scoreField(token, doc.titleLc);
              total += hayScore + (titleScore > 0 ? titleScore * 2 : 0);
            }
            return total;
          };

          const highlight = (text, tokens) => {
            if (!tokens.length) {
              return escapeHtml(text);
            }
            const lower = text.toLowerCase();
            const ranges = [];
            for (const token of tokens) {
              let from = 0;
              let at = lower.indexOf(token, from);
              while (at !== -1) {
                ranges.push([at, at + token.length]);
                from = at + token.length;
                at = lower.indexOf(token, from);
              }
            }
            if (!ranges.length) {
              return escapeHtml(text);
            }
            ranges.sort((a, b) => a[0] - b[0]);
            const merged = [];
            for (const range of ranges) {
              const last = merged[merged.length - 1];
              if (last && range[0] <= last[1]) {
                last[1] = Math.max(last[1], range[1]);
              } else {
                merged.push(range.slice());
              }
            }
            let html = "";
            let pos = 0;
            for (const [start, end] of merged) {
              html += escapeHtml(text.slice(pos, start));
              html += "<mark>" + escapeHtml(text.slice(start, end)) + "</mark>";
              pos = end;
            }
            html += escapeHtml(text.slice(pos));
            return html;
          };

          const safeUrl = (url) => {
            const value = String(url || "");
            if (/^(https?:\/\/|\/|\.\/|\.\.\/|#)/.test(value)) {
              return value;
            }
            return "#";
          };

          const matchTag = (doc) => !normalizedSelectedTag
            || (doc.tagsNormalized || []).includes(normalizedSelectedTag);

          const render = (entries, tokens, query, total) => {
            if (!entries.length) {
              results.innerHTML = "";
              if (query && selectedTag) {
                status.textContent = fmt(MESSAGES.noMatchInTag, { tag: selectedTag, query: query });
              } else if (query) {
                status.textContent = fmt(MESSAGES.noMatch, { query: query });
              } else if (selectedTag) {
                status.textContent = fmt(MESSAGES.noPostsInTag, { tag: selectedTag });
              } else {
                status.textContent = MESSAGES.noPosts;
              }
              return;
            }

            const matched = total ?? entries.length;
            const topSuffix = matched > entries.length ? fmt(MESSAGES.showingTopSuffix, { shown: entries.length }) : "";
            if (query && selectedTag) {
              status.textContent = fmt(MESSAGES.hitsInTag, { count: matched, tag: selectedTag }) + topSuffix;
            } else if (query) {
              status.textContent = fmt(MESSAGES.hits, { count: matched }) + topSuffix;
            } else if (selectedTag) {
              status.textContent = fmt(MESSAGES.taggedPosts, { count: matched, tag: selectedTag });
            } else {
              status.textContent = fmt(MESSAGES.browseAll, { count: docs.length });
            }

            const rows = entries.map(({ doc }) => (
              '<article class="archive-row">' +
              "<time>" + escapeHtml(doc.date) + "</time>" +
              '<h3><a href="' + escapeHtml(safeUrl(doc.url)) + '">' + highlight(doc.title, tokens) + "</a></h3>" +
              "<p>" + highlight(doc.summary, tokens) + "</p>" +
              "</article>"
            ));
            results.innerHTML = rows.join("");
          };

          const search = (raw) => {
            if (!docs) {
              return;
            }
            const query = raw.trim();
            const normalized = normalizeText(query);
            const scopedDocs = docs.filter((doc) => matchTag(doc));
            if (!normalized) {
              render(scopedDocs.slice(0, RECENT_LIMIT).map((doc) => ({ doc, score: 0 })), [], "", scopedDocs.length);
              return;
            }
            const tokens = normalized.split(/\s+/).filter(Boolean);
            const matches = [];
            for (const doc of scopedDocs) {
              const score = scoreDoc(tokens, doc);
              if (score >= 0) {
                matches.push({ doc, score });
              }
            }
            matches.sort((a, b) => b.score - a.score || a.doc.title.localeCompare(b.doc.title));
            render(matches.slice(0, RESULT_LIMIT), tokens, query, matches.length);
          };

          const prepare = (data) => {
            docs = (data.documents || []).map((doc) => ({
              title: doc.title || "",
              summary: doc.summary || "",
              tags: doc.tags || [],
              tagsNormalized: (doc.tags || []).map((tag) => normalizeText(tag)),
              url: doc.url || "#",
              date: doc.date || "",
              titleLc: normalizeText(doc.title),
              hay: normalizeText([doc.title, doc.summary, (doc.tags || []).join(" "), doc.body]
                .join(" \n ")),
            }));
            if (scope && selectedTag) {
              scope.hidden = false;
              scope.textContent = fmt(MESSAGES.scopeTag, { tag: selectedTag });
            }
            search(input.value);
          };

          let timer = 0;
          input.addEventListener("input", () => {
            window.clearTimeout(timer);
            timer = window.setTimeout(() => {
              search(input.value);
              const params = new URLSearchParams(window.location.search);
              const value = input.value.trim();
              if (value) {
                params.set("q", value);
              } else {
                params.delete("q");
              }
              const next = params.toString();
              window.history.replaceState(null, "", next ? "?" + next : window.location.pathname);
            }, 90);
          });

          input.form?.addEventListener("submit", (event) => {
            event.preventDefault();
            search(input.value);
          });

          const initial = new URLSearchParams(window.location.search).get("q");
          if (initial) {
            input.value = initial;
          }

          const load = () => {
            fetch(indexUrl, { cache: "no-cache" })
              .then((response) => {
                if (!response.ok) {
                  throw new Error("failed to load search index");
                }
                return response.json();
              })
              .then(prepare)
              .catch(() => {
                status.textContent = MESSAGES.loadError;
              });
          };

          if ("requestIdleCallback" in window) {
            window.requestIdleCallback(load, { timeout: 1000 });
          } else {
            window.setTimeout(load, 0);
          }
        })();
        """;

    private static string BuildSiteScript() => """
        (() => {
          "use strict";

          const menuToggle = document.querySelector("[data-site-menu-toggle]");
          const siteNav = document.querySelector("[data-site-nav]");

          if (menuToggle && siteNav) {
            const setMenuOpen = (isOpen) => {
              menuToggle.setAttribute("aria-expanded", String(isOpen));
              siteNav.classList.toggle("site-nav-open", isOpen);
            };

            const closeMenu = () => {
              setMenuOpen(false);
            };

            menuToggle.addEventListener("click", () => {
              const isOpen = menuToggle.getAttribute("aria-expanded") === "true";
              setMenuOpen(!isOpen);
            });

            for (const link of siteNav.querySelectorAll("a")) {
              link.addEventListener("click", () => {
                closeMenu();
              });
            }

            document.addEventListener("click", (event) => {
              const target = event.target;
              if (!(target instanceof Node)) {
                return;
              }
              if (!siteNav.contains(target) && !menuToggle.contains(target)) {
                closeMenu();
              }
            });

            document.addEventListener("keydown", (event) => {
              if (event.key === "Escape" && menuToggle.getAttribute("aria-expanded") === "true") {
                closeMenu();
                menuToggle.focus();
              }
            });

            window.addEventListener("resize", closeMenu);

            closeMenu();
          }

          const toc = document.querySelector(".post-toc");
          if (!toc) {
            return;
          }

          const nav = toc.querySelector(".toc-nav");
          const links = Array.from(toc.querySelectorAll("[data-toc-link]"));
          const indicator = toc.querySelector(".toc-indicator");
          if (!nav || !indicator || links.length === 0) {
            return;
          }

          const entries = links
            .map((link) => {
              const href = link.getAttribute("href") || "";
              const id = href.startsWith("#") ? href.slice(1) : "";
              const heading = id ? document.getElementById(id) : null;
              const item = link.closest("[data-toc-item]");
              return heading && item ? { link, heading, item } : null;
            })
            .filter(Boolean);

          if (entries.length === 0) {
            return;
          }

          let active = entries[0];

          const updateIndicator = (entry) => {
            active = entry;
            for (const candidate of entries) {
              const isActive = candidate === entry;
              candidate.link.classList.toggle("toc-link-active", isActive);
              candidate.item.classList.toggle("toc-item-active", isActive);
              if (isActive) {
                candidate.link.setAttribute("aria-current", "location");
              }
              else {
                candidate.link.removeAttribute("aria-current");
              }
            }

            const navRect = nav.getBoundingClientRect();
            const itemRect = entry.item.getBoundingClientRect();
            indicator.style.opacity = "1";
            indicator.style.transform = `translateY(${Math.max(0, itemRect.top - navRect.top + 8)}px)`;
            indicator.style.height = `${Math.max(24, itemRect.height - 4)}px`;
          };

          const pickActive = () => {
            const threshold = window.innerHeight * 0.24;
            let current = entries[0];
            for (const entry of entries) {
              const rect = entry.heading.getBoundingClientRect();
              if (rect.top <= threshold) {
                current = entry;
              }
              else {
                break;
              }
            }

            updateIndicator(current);
          };

          let rafId = 0;
          const requestUpdate = () => {
            if (rafId !== 0) {
              return;
            }

            rafId = window.requestAnimationFrame(() => {
              rafId = 0;
              pickActive();
            });
          };

          window.addEventListener("scroll", requestUpdate, { passive: true });
          window.addEventListener("resize", requestUpdate);
          requestUpdate();
        })();
        """;

    private sealed class Utf8StringWriter : StringWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
    }
}
