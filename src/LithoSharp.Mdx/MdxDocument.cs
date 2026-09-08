using System.Text;
using System.Security.Cryptography;
using LithoSharp.Content;
using LithoSharp.Diagnostics;

namespace LithoSharp.Mdx;

/// <summary>Unexecuted MDX source with its original line positions.</summary>
public sealed class MdxDocument : IHtmlContent
{
    /// <summary>Creates a source document. The first body line is one-based.</summary>
    public MdxDocument(string body, int bodyStartLine = 1)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentOutOfRangeException.ThrowIfLessThan(bodyStartLine, 1);
        Body = body;
        BodyStartLine = bodyStartLine;
    }
    /// <summary>The unmodified MDX body, excluding YAML.</summary>
    public string Body { get; }
    /// <summary>The original first line of the body.</summary>
    public int BodyStartLine { get; }
    internal string CompilerSource => new string('\n', BodyStartLine - 1) + Body;
    internal string? RenderedHtml { get; init; }
    /// <summary>Returns the prepared React fragment, or fails if this document has not been compiled.</summary>
    public string ToHtmlString() => RenderedHtml ?? throw new InvalidOperationException("Register the collection with MdxSite before rendering its body.");
}

/// <summary>Loads MDX into existing typed collections using the strict C# YAML binder.</summary>
public sealed class MdxContentCollectionLoader<TFrontMatter> : IContentCollectionLoader<TFrontMatter, MdxDocument>
    where TFrontMatter : notnull
{
    private readonly ContentCollectionId id;
    private readonly string inputRoot;
    private readonly ContentRouteConvention<TFrontMatter, MdxDocument> route;
    private readonly ContentPublicationMapper<TFrontMatter, MdxDocument> publication;
    private readonly IContentFrontMatterBinder<TFrontMatter> binder;

    /// <summary>Creates an MDX loader. Files with an underscore-prefixed path segment are import-only partials.</summary>
    public MdxContentCollectionLoader(ContentCollectionId id, string inputRoot,
        ContentRouteConvention<TFrontMatter, MdxDocument> routeConvention,
        ContentPublicationMapper<TFrontMatter, MdxDocument> publicationMapper,
        IContentFrontMatterBinder<TFrontMatter>? frontMatterBinder = null)
    {
        this.id = id ?? throw new ArgumentNullException(nameof(id));
        ArgumentException.ThrowIfNullOrWhiteSpace(inputRoot);
        this.inputRoot = Path.GetFullPath(inputRoot);
        route = routeConvention ?? throw new ArgumentNullException(nameof(routeConvention));
        publication = publicationMapper ?? throw new ArgumentNullException(nameof(publicationMapper));
        binder = frontMatterBinder ?? new ReflectionContentFrontMatterBinder<TFrontMatter>();
    }

    /// <summary>Explicitly interprets .md files as MDX in this collection. The default is false.</summary>
    public bool IncludeMarkdown { get; init; }
    /// <summary>A stable identifier for the caller's route and publication rules. Null disables caching.</summary>
    public string? TransformationFingerprint { get; init; }

    /// <inheritdoc />
    public async ValueTask<ContentLoadResult<TFrontMatter, MdxDocument>> LoadAsync(CancellationToken cancellationToken = default)
    {
        var discovery = ContentPath.Discover(inputRoot, IncludeMarkdown ? [".mdx", ".md"] : [".mdx"], cancellationToken);
        var diagnostics = discovery.Diagnostics.ToList();
        var entries = new List<ContentEntry<TFrontMatter, MdxDocument>>();
        var markdown = new MarkdownContentCollectionLoader<TFrontMatter>(id, inputRoot,
            _ => throw new InvalidOperationException("MDX uses its own route convention."),
            _ => throw new InvalidOperationException("MDX uses its own publication mapper."), binder);
        foreach (var file in discovery.Files.Where(file => !IsPartial(file.RelativePath)))
        {
            var result = await markdown.LoadEntryAsync(file.FullPath, cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(result.Diagnostics);
            if (!result.IsSuccess) continue;
            var entry = result.Value!;
            var bytes = await ContentPath.ReadAllBytesAsync(inputRoot, file.FullPath, cancellationToken).ConfigureAwait(false);
            if (entry.SourceFingerprint != "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)))
                throw new IOException("MDX source changed while loading; retry the build.");
            var original = new UTF8Encoding(false, true).GetString(bytes);
            var start = original.AsSpan(0, original.Length - entry.Body.Length).Count('\n') + 1;
            entries.Add(new(entry.Id, entry.SourcePath, entry.SourceFingerprint, entry.FrontMatter,
                new MdxDocument(entry.Body, start), entry.SourceLocation));
        }
        if (diagnostics.Any(diagnostic => diagnostic.Severity == SiteDiagnosticSeverity.Error))
            return ContentLoadResult<TFrontMatter, MdxDocument>.Failure(diagnostics);
        return ContentLoadResult<TFrontMatter, MdxDocument>.Success(new(id, inputRoot, entries, route, publication,
            transformationId: TransformationFingerprint is null ? null : new(TransformationFingerprint),
            isCacheable: TransformationFingerprint is not null), diagnostics);
    }

    internal static bool IsPartial(string path) => path.Split('/').Any(segment => segment.StartsWith('_'));
}
