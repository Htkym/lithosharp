using System.Text.Json;
using LithoSharp.Build;
using LithoSharp.Content;
using LithoSharp.Content.Compilation;
using LithoSharp.Diagnostics;
using LithoSharp.Inspection;

namespace LithoSharp.Mdx;

/// <summary>MDX import宣言の1件を表します。解決も実行もしていません。</summary>
public sealed class MdxImportInfo
{
    internal MdxImportInfo(string specifier, IReadOnlyList<string> names, SiteSourceLocation? location)
    {
        Specifier = specifier;
        Names = Array.AsReadOnly(names.ToArray());
        Location = location;
    }

    /// <summary>import元を取得します。</summary>
    public string Specifier { get; }

    /// <summary>導入名を取得します。</summary>
    public IReadOnlyList<string> Names { get; }

    /// <summary>宣言の行精度の位置を取得します。列は不明です。</summary>
    public SiteSourceLocation? Location { get; }
}

/// <summary>MDX非実行inspectionの要求条件を表します。</summary>
/// <param name="DocumentVersion">文書版数。最新判定に使います。</param>
/// <param name="ProjectGeneration">project generation。取得済みcontextと対応付けます。</param>
public sealed record MdxAnalysisOptions(long DocumentVersion = 0, long ProjectGeneration = 0);

/// <summary>MDX非実行inspectionの1回の結果を表します。</summary>
public sealed class MdxAnalysisResult
{
    internal MdxAnalysisResult(
        string sourcePath,
        long documentVersion,
        long projectGeneration,
        string stage,
        string? title,
        string plainText,
        IReadOnlyList<DocumentHeadingInfo> headings,
        IReadOnlyList<DocumentLinkInfo> links,
        IReadOnlyList<DocumentAssetInfo> assets,
        IReadOnlyList<DocumentComponentInfo> components,
        IReadOnlyList<MdxImportInfo> imports,
        IReadOnlyList<SiteDiagnostic> diagnostics)
    {
        SourcePath = sourcePath;
        DocumentVersion = documentVersion;
        ProjectGeneration = projectGeneration;
        Stage = stage;
        Title = title;
        PlainText = plainText;
        Headings = Array.AsReadOnly(headings.ToArray());
        Links = Array.AsReadOnly(links.ToArray());
        Assets = Array.AsReadOnly(assets.ToArray());
        Components = Array.AsReadOnly(components.ToArray());
        Imports = Array.AsReadOnly(imports.ToArray());
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
    }

    /// <summary>解析したsource pathを取得します。</summary>
    public string SourcePath { get; }

    /// <summary>要求した文書版数を取得します。</summary>
    public long DocumentVersion { get; }

    /// <summary>要求したproject generationを取得します。</summary>
    public long ProjectGeneration { get; }

    /// <summary>解析段階を取得します。常に syntax です。</summary>
    public string Stage { get; }

    /// <summary>文書題を取得します。ない場合は <see langword="null"/> です。</summary>
    public string? Title { get; }

    /// <summary>静的テキストを取得します。</summary>
    public string PlainText { get; }

    /// <summary>見出しを取得します。構文未完成では空です。</summary>
    public IReadOnlyList<DocumentHeadingInfo> Headings { get; }

    /// <summary>リンクと画像を取得します。構文未完成では空です。</summary>
    public IReadOnlyList<DocumentLinkInfo> Links { get; }

    /// <summary>asset参照を取得します。</summary>
    public IReadOnlyList<DocumentAssetInfo> Assets { get; }

    /// <summary>island component参照を取得します。</summary>
    public IReadOnlyList<DocumentComponentInfo> Components { get; }

    /// <summary>import宣言を取得します。解決も実行もしません。</summary>
    public IReadOnlyList<MdxImportInfo> Imports { get; }

    /// <summary>解析で得た診断を取得します。構文未完成ではfatal診断のみです。</summary>
    public IReadOnlyList<SiteDiagnostic> Diagnostics { get; }
}

/// <summary>未保存MDXの非実行inspection sessionを表します。</summary>
/// <remarks>
/// 編集用の独立したworkerを所有し、serve/buildの実行workerと取消・寿命を分離します。
/// 解析はbuildと同じ前処理・MDX parser・許可済み構文変換を使いますが、import先moduleの実行、
/// SSR・evaluate・run・bundle、ユーザー設定/plugin読み込み、network取得を行いません。
/// Markdownとして誤解釈せず、Markdown parserへのfallbackも作りません。
/// TypeScriptはtranspileも型検査もしません（workerは型検査を一切行いません）。
/// </remarks>
public sealed class MdxInspectionSession : IAsyncDisposable
{
    private readonly MdxOptions options;
    private readonly MdxWorker worker;
    private readonly SemaphoreSlim lifetime = new(1, 1);
    private readonly CancellationTokenSource ownerCancellation = new();
    private readonly TaskCompletionSource<bool> disposalCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int disposalStarted;

    /// <summary>編集用の検査sessionを作成します。Nodeは初回解析まで起動しません。</summary>
    public MdxInspectionSession(MdxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
        if (options.Timeout <= TimeSpan.Zero || options.MaximumMessageBytes < 1024)
            throw new ArgumentException("Worker timeout and message size must be positive.", nameof(options));
        worker = new MdxWorker(options);
    }

    internal int WorkerStarts => worker.Starts;

    /// <summary>未保存を含むMDX文書を非実行で解析します。</summary>
    /// <param name="sourcePath">文書のsource path。位置情報の識別に使います。</param>
    /// <param name="text">文書全体のテキスト。front matterを含む形式です。</param>
    /// <param name="analysisOptions">文書版数とproject generation。ない場合は既定値です。</param>
    /// <param name="cancellationToken">取消token。入力取消でbuild側workerを止めません。</param>
    /// <returns>見出し・リンク・asset・component・import宣言・診断のsnapshot。公開出力も元ファイルも更新しません。</returns>
    /// <exception cref="SiteBuildExtensionException">worker異常時は診断付きで失敗します。</exception>
    public async Task<MdxAnalysisResult> AnalyzeAsync(
        string sourcePath,
        string text,
        MdxAnalysisOptions? analysisOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(text);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposalStarted) != 0, this);
        using var analysisCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ownerCancellation.Token);
        await lifetime.WaitAsync(analysisCancellation.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposalStarted) != 0, this);
            analysisCancellation.Token.ThrowIfCancellationRequested();
            return await AnalyzeCoreAsync(sourcePath, text, analysisOptions, analysisCancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            lifetime.Release();
        }
    }

    private async Task<MdxAnalysisResult> AnalyzeCoreAsync(
        string sourcePath,
        string text,
        MdxAnalysisOptions? analysisOptions,
        CancellationToken cancellationToken)
    {
        var split = FrontMatterSplitter.TrySplit(text, cancellationToken);
        var frontMatterDiagnostics = new List<SiteDiagnostic>();
        if (split.Status == FrontMatterSplitStatus.Ok)
        {
            // Parse only the supplied YAML. This shares the strict build parser;
            // it does not bind a user type or execute project/module/plugin code.
            var parsed = MarkdownContentCollectionLoader<object>.ParseYaml(
                split.Yaml, sourcePath, 2, cancellationToken);
            if (!parsed.IsSuccess) frontMatterDiagnostics.AddRange(parsed.Diagnostics);
        }
        else if (split.Status == FrontMatterSplitStatus.EmptyFrontMatter)
        {
            frontMatterDiagnostics.Add(new SiteDiagnostic(
                MarkdownContentDiagnosticIds.EmptyFrontMatter, SiteDiagnosticSeverity.Error,
                "YAML front matter must not be empty.", new SiteSourceLocation(sourcePath, 2, 1)));
        }
        else if (split.Status == FrontMatterSplitStatus.UnterminatedFrontMatter)
        {
            frontMatterDiagnostics.Add(new SiteDiagnostic(
                MarkdownContentDiagnosticIds.UnterminatedFrontMatter, SiteDiagnosticSeverity.Error,
                "Markdown document has no closing front matter marker.",
                new SiteSourceLocation(sourcePath, split.FailureLine, 1)));
        }
        var locator = new SourceText(text);
        MdxDocument document = split.Status switch
        {
            FrontMatterSplitStatus.Ok or FrontMatterSplitStatus.EmptyFrontMatter => new MdxDocument(
                split.Body, locator.GetLineAndColumn(split.BodyStartOffset).Line, split.BodyStartOffset),
            FrontMatterSplitStatus.MissingFrontMatter or FrontMatterSplitStatus.UnterminatedFrontMatter =>
                new MdxDocument(text, 1, 0),
            _ => new MdxDocument(split.Body, 1, 0),
        };

        var requestId = Guid.NewGuid().ToString("N");
        var response = await worker.SendAsync(new
        {
            protocol = 1,
            type = "analyze",
            requestId,
            sourcePath,
            text = document.CompilerSource,
        }, requestId, cancellationToken).ConfigureAwait(false);
        if (!response.GetProperty("success").GetBoolean())
        {
            throw new SiteBuildExtensionException(response.GetProperty("diagnostics").EnumerateArray().Select(diagnostic =>
                new SiteDiagnostic(diagnostic.GetProperty("id").GetString()!, SiteDiagnosticSeverity.Error,
                    diagnostic.GetProperty("message").GetString()!, ReadWorkerLocation(diagnostic))));
        }

        var result = response.GetProperty("result");
        var semantics = MdxSemantics.FromWorker(
            sourcePath,
            document,
            result.GetProperty("headings"),
            result.GetProperty("links"),
            result.GetProperty("islands"),
            result.GetProperty("text").GetString());
        return new MdxAnalysisResult(
            sourcePath,
            analysisOptions?.DocumentVersion ?? 0,
            analysisOptions?.ProjectGeneration ?? 0,
            "syntax",
            semantics.Title,
            semantics.PlainText,
            semantics.Headings.Select(heading => new DocumentHeadingInfo(heading.Text, heading.Id, heading.RawLevel, heading.OutputLevel, Locate(locator, sourcePath, heading.Span))).ToArray(),
            semantics.Links.Select(link => new DocumentLinkInfo(link.RawText, link.Url, link.Title, link.IsImage, Locate(locator, sourcePath, link.Span))).ToArray(),
            semantics.Assets.Select(asset => new DocumentAssetInfo(asset.Url, Locate(locator, sourcePath, asset.Span))).ToArray(),
            semantics.Components.Select(component => new DocumentComponentInfo(component.Name, Locate(locator, sourcePath, component.Span))).ToArray(),
            ReadImports(result.GetProperty("imports"), sourcePath),
            frontMatterDiagnostics.Concat(ReadDiagnostics(result.GetProperty("diagnostics"), sourcePath)).ToArray());
    }

    private static IReadOnlyList<MdxImportInfo> ReadImports(JsonElement imports, string sourcePath)
    {
        var list = new List<MdxImportInfo>();
        if (imports.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in imports.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var specifier = item.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.String
                ? source.GetString() ?? string.Empty : string.Empty;
            var names = item.TryGetProperty("names", out var namesValue) && namesValue.ValueKind == JsonValueKind.Array
                ? namesValue.EnumerateArray().Where(name => name.ValueKind == JsonValueKind.String).Select(name => name.GetString()!).ToArray()
                : [];
            var line = item.TryGetProperty("line", out var lineValue) && lineValue.ValueKind == JsonValueKind.Number && lineValue.TryGetInt32(out var number)
                ? number : (int?)null;
            list.Add(new MdxImportInfo(specifier, names,
                line is null ? null : new SiteSourceLocation(sourcePath, line, null, line, null)));
        }

        return list;
    }

    private static IReadOnlyList<SiteDiagnostic> ReadDiagnostics(JsonElement diagnostics, string sourcePath)
    {
        var list = new List<SiteDiagnostic>();
        if (diagnostics.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in diagnostics.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var message = item.TryGetProperty("message", out var messageValue) && messageValue.ValueKind == JsonValueKind.String
                ? messageValue.GetString() ?? string.Empty : string.Empty;
            var line = item.TryGetProperty("line", out var lineValue) && lineValue.ValueKind == JsonValueKind.Number
                && lineValue.TryGetInt32(out var lineNumber) && lineNumber > 0 ? lineNumber : (int?)null;
            var column = item.TryGetProperty("column", out var columnValue) && columnValue.ValueKind == JsonValueKind.Number
                && columnValue.TryGetInt32(out var columnNumber) && columnNumber >= 0 ? columnNumber + 1 : (int?)null;
            var location = line is null
                ? new SiteSourceLocation(sourcePath)
                : new SiteSourceLocation(sourcePath, line, column);
            list.Add(new SiteDiagnostic("LSMDX001", SiteDiagnosticSeverity.Error, message,
                location));
        }

        return list;
    }

    private static SiteSourceLocation? ReadWorkerLocation(JsonElement diagnostic)
    {
        var file = diagnostic.TryGetProperty("file", out var fileValue) && fileValue.ValueKind == JsonValueKind.String
            ? fileValue.GetString()
            : null;
        if (string.IsNullOrEmpty(file)) return null;
        var line = diagnostic.TryGetProperty("line", out var lineValue) && lineValue.ValueKind == JsonValueKind.Number
            && lineValue.TryGetInt32(out var lineNumber) && lineNumber > 0 ? lineNumber : (int?)null;
        var column = diagnostic.TryGetProperty("column", out var columnValue) && columnValue.ValueKind == JsonValueKind.Number
            && columnValue.TryGetInt32(out var columnNumber) && columnNumber > 0 ? columnNumber : (int?)null;
        return line is null ? new SiteSourceLocation(file) : new SiteSourceLocation(file, line, column);
    }

    private static SiteSourceLocation? Locate(SourceText locator, string sourcePath, SourceSpan span)
    {
        // Empty is the semantic model's unknown location, not a point at1:1.
        if (span.IsEmpty) return null;
        if (span.End <= locator.Text.Length)
        {
            var (line, column) = locator.GetLineAndColumn(span.Start);
            var (endLine, endColumn) = locator.GetLineAndColumn(span.End);
            return new SiteSourceLocation(sourcePath, line, column, endLine, endColumn);
        }

        return new SiteSourceLocation(sourcePath);
    }

    /// <summary>所有する編集用workerを破棄します。</summary>
    public ValueTask DisposeAsync()
    {
        // Publish disposal before canceling or waiting for the active analysis.
        // Every disposer awaits the same owned cleanup, including its failure.
        if (Interlocked.CompareExchange(ref disposalStarted, 1, 0) == 0)
            _ = DisposeCoreAsync();
        return new ValueTask(disposalCompletion.Task);
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            ownerCancellation.Cancel();
            await lifetime.WaitAsync().ConfigureAwait(false);
            try
            {
                await worker.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                lifetime.Release();
            }
        }
        catch (Exception error)
        {
            disposalCompletion.TrySetException(error);
            return;
        }
        finally
        {
            ownerCancellation.Dispose();
        }
        disposalCompletion.TrySetResult(true);
    }
}
