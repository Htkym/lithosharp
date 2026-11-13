using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LithoSharp.Build;
using LithoSharp.Content;
using LithoSharp.Pages;
using LithoSharp.Routing;

namespace LithoSharp.Documentation;

/// <summary>A documented API identity, including overload and generic XML IDs. Equality uses the five positional values; use <see cref="ApiReferenceSite.Compare"/> to compare symbol facts.</summary>
public sealed record ApiMember(string Id, string Title, string Summary, string BodyHtml, bool Deprecated = false)
{
    /// <summary>Explicit symbol facts, or null when the input supplies documentation only.</summary>
    public ApiSymbol? Symbol { get; init; }
    /// <summary>The explicit assembly identity, when a symbol source was registered.</summary>
    public string? AssemblyName { get; init; }
    internal string? DocumentationContent { get; init; }
    internal string? ContractContent { get; init; }
    internal string? ObsoleteContent { get; init; }
    internal string? ParsedBodyHtml { get; init; }
    /// <summary>Compares the original documented values independently of symbol source objects and internal comparison data.</summary>
    public bool Equals(ApiMember? other) => other is not null && Id == other.Id && Title == other.Title && Summary == other.Summary
        && BodyHtml == other.BodyHtml && Deprecated == other.Deprecated;
    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Id, Title, Summary, BodyHtml, Deprecated);
}
/// <summary>A change between two explicitly selected API snapshots.</summary>
/// <param name="Id">The exact API identity.</param>
/// <param name="Kind">One of added, removed, signature, visibility, deprecated, obsolete, documentation or symbol-information. A category alone does not assert that a change is breaking.</param>
public sealed record ApiChange(string Id, string Kind);
/// <summary>An API input with the same identity and route dimensions as documentation.</summary>
public sealed record ApiReferenceInput(string Collection, DocumentVariant Variant, string FilePath);

/// <summary>XML comments and OpenAPI providers using typed pages and the common output transaction.</summary>
public sealed class ApiReferenceSite : ISiteBuildExtension
{
    private readonly List<(ApiReferenceInput Input, bool OpenApi, ApiAssemblySource? Source)> inputs = [];
    private readonly Dictionary<DocumentKey, SiteUrl> referenceMap = [];
    private readonly List<(string Collection, string Previous, string Current, string Locale, string Route)> comparisons = [];
    /// <summary>Registers a derived comparison page between two explicitly loaded API variants.</summary>
    public void AddComparison(string collection, string previousVersion, string currentVersion, string locale, string routePrefix) => comparisons.Add((collection, previousVersion, currentVersion, locale, routePrefix));
    private IReadOnlyDictionary<DocumentKey, ApiMember> members = new Dictionary<DocumentKey, ApiMember>();
    private IReadOnlyDictionary<DocumentKey, SiteRoute> routes = new Dictionary<DocumentKey, SiteRoute>();
    /// <summary>An optional trusted layout for member, index and comparison pages. Null uses the standard document renderer.</summary>
    public IPageLayout<PageLayoutContent>? Layout { get; init; }
    /// <summary>A stable identity for the custom layout's configuration. Required when Layout is set; layout code identity is captured separately.</summary>
    public string? LayoutFingerprint { get; init; }
    /// <summary>The last prepared member catalog, keyed by collection, version, locale and exact API ID.</summary>
    public IReadOnlyDictionary<DocumentKey, ApiMember> Members => members;
    /// <summary>Registers XML documentation. Existing files are read; project code is not executed.</summary>
    public void AddXml(ApiReferenceInput input) => inputs.Add((input ?? throw new ArgumentNullException(nameof(input)), false, null));
    /// <summary>Registers XML with caller-supplied assembly symbols and exact cross-assembly references. Symbols are not automatically extracted from projects or assemblies.</summary>
    public void AddXml(ApiReferenceInput input, ApiAssemblySource source) => inputs.Add((input ?? throw new ArgumentNullException(nameof(input)), false, source ?? throw new ArgumentNullException(nameof(source))));
    /// <summary>Registers explicit external API URLs. No remote documentation is downloaded.</summary>
    public void AddReferenceMap(IReadOnlyDictionary<DocumentKey, SiteUrl> references)
    {
        ArgumentNullException.ThrowIfNull(references);
        foreach (var (key, url) in references)
        {
            ArgumentNullException.ThrowIfNull(key);
            ArgumentNullException.ThrowIfNull(url);
            if (referenceMap.ContainsKey(key))
                throw new ArgumentException("Duplicate external API reference: " + key, nameof(references));
        }
        foreach (var (key, url) in references)
            referenceMap.Add(key, url);
    }
    /// <summary>Registers OpenAPI 3 JSON. External references are diagnosed instead of fetched.</summary>
    public void AddOpenApi(ApiReferenceInput input) => inputs.Add((input ?? throw new ArgumentNullException(nameof(input)), true, null));
    /// <summary>Resolves an exact type, member, overload or generic ID in an explicit document variant.</summary>
    public PageRef<ApiMember> Resolve(DocumentKey key)
    {
        var route = routes.TryGetValue(key, out var found) ? found : throw new KeyNotFoundException("Missing API reference: " + key);
        var collection = new ContentCollectionId($"api:{key.Collection}:{key.Version}:{key.Locale}");
        return new(new(ContentPageIdentity.Create(collection, key.Id)), route);
    }
    /// <summary>Separates additions/removals, signatures, visibility, obsolete information and documentation changes.</summary>
    public static IReadOnlyList<ApiChange> Compare(IEnumerable<ApiMember> previous, IEnumerable<ApiMember> current)
    {
        var before = previous.ToDictionary(member => member.Id, StringComparer.Ordinal);
        var after = current.ToDictionary(member => member.Id, StringComparer.Ordinal);
        var changes = new List<ApiChange>();
        foreach (var id in before.Keys.Union(after.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if (!before.TryGetValue(id, out var oldMember))
                changes.Add(new(id, "added"));
            else if (!after.TryGetValue(id, out var newMember))
                changes.Add(new(id, "removed"));
            else
            {
                var oldSymbol = oldMember.Symbol;
                var newSymbol = newMember.Symbol;
                if ((oldSymbol is null) != (newSymbol is null)
                    || (oldMember.ContractContent is null) != (newMember.ContractContent is null)
                    || (oldSymbol is not null && newSymbol is not null && (oldSymbol.ReturnType is null) != (newSymbol.ReturnType is null)))
                    changes.Add(new(id, "symbol-information"));
                if (oldSymbol is not null && newSymbol is not null)
                {
                    if (oldSymbol.Signature != newSymbol.Signature || (oldSymbol.ReturnType is not null && newSymbol.ReturnType is not null && oldSymbol.ReturnType != newSymbol.ReturnType)
                        || !oldSymbol.Parameters.Select(parameter => (parameter.Name, parameter.Type)).SequenceEqual(newSymbol.Parameters.Select(parameter => (parameter.Name, parameter.Type)))
                        || !oldSymbol.GenericConstraints.SequenceEqual(newSymbol.GenericConstraints, StringComparer.Ordinal)
                        || !oldSymbol.Inheritance.SequenceEqual(newSymbol.Inheritance, StringComparer.Ordinal))
                        changes.Add(new(id, "signature"));
                    if (oldSymbol.Visibility != newSymbol.Visibility)
                        changes.Add(new(id, "visibility"));
                }
                if (oldMember.ContractContent is not null && newMember.ContractContent is not null && oldMember.ContractContent != newMember.ContractContent)
                    changes.Add(new(id, "signature"));
                if (oldMember.Deprecated != newMember.Deprecated
                    || (oldSymbol is not null && newSymbol is not null && oldSymbol.ObsoleteMessage != newSymbol.ObsoleteMessage)
                    || (oldMember.ObsoleteContent is not null && newMember.ObsoleteContent is not null && oldMember.ObsoleteContent != newMember.ObsoleteContent))
                    changes.Add(new(id, !oldMember.Deprecated && newMember.Deprecated ? "deprecated" : "obsolete"));
                if (oldMember.Title != newMember.Title || oldMember.Summary != newMember.Summary
                    || (oldMember.DocumentationContent is not null && newMember.DocumentationContent is not null
                        && oldMember.BodyHtml == oldMember.ParsedBodyHtml && newMember.BodyHtml == newMember.ParsedBodyHtml
                        ? oldMember.DocumentationContent != newMember.DocumentationContent : oldMember.BodyHtml != newMember.BodyHtml))
                    changes.Add(new(id, "documentation"));
            }
        }
        return changes;
    }
    /// <summary>A stable output route derived from the exact XML or OpenAPI identity.</summary>
    public static SiteRoute RouteFor(string prefix, string id, string? baseUrl = null) => SiteRoute.ForDirectoryIndex(prefix.Trim('/') + "/" +
        System.Text.RegularExpressions.Regex.Replace(id.Split('(')[0], @"[^\p{L}\p{N}_.-]", "-") + "-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..12], baseUrl);

    /// <inheritdoc />
    public async Task<SiteBuildContribution> PrepareAsync(SiteBuildContext context, CancellationToken cancellationToken = default)
    {
        var layout = Layout;
        var layoutFingerprint = LayoutFingerprint;
        if (layout is not null && string.IsNullOrWhiteSpace(layoutFingerprint))
            throw new ArgumentException("An explicit stable LayoutFingerprint is required for a custom API layout.", nameof(LayoutFingerprint));
        var layoutIdentity = layout is null ? "standard" : SiteContentCollection.CaptureRendererIdentity(
            (Func<SitePage<PageLayoutContent>, PageRenderingContext, IHtmlContent>)layout.Render, layout.GetType()) + "|" + layoutFingerprint;
        string Render(ContentPageRenderingContext rendering, string body, string pageId)
        {
            if (layout is null)
                return rendering.RenderDocument(body);
            rendering.SetDerivedContent(body);
            var page = new SitePage<PageLayoutContent>(new(pageId), rendering.Route, new(Html.UnsafeRaw(body)), rendering.Metadata);
            return (layout.Render(page, rendering.CreateLayoutContext())
                ?? throw new InvalidOperationException("The API page layout returned null.")).ToHtmlString();
        }
        var loaded = new List<(ApiReferenceInput Input, byte[] Bytes, string Hash, bool OpenApi, ApiAssemblySource? Source)>();
        foreach (var (input, openApi, source) in inputs)
        {
            var path = Path.GetFullPath(input.FilePath);
            var bytes = await ContentPath.ReadAllBytesAsync(Path.GetDirectoryName(path)!, path, cancellationToken).ConfigureAwait(false);
            loaded.Add((input, bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)), openApi, source));
        }
        var xml = new ApiXmlReferenceReader(loaded.Where(item => !item.OpenApi).Select(item => (item.Input, item.Bytes, item.Source)));
        var allMembers = new Dictionary<DocumentKey, ApiMember>();
        var allRoutes = new Dictionary<DocumentKey, SiteRoute>();
        void AddRoute(ApiReferenceInput input, string id)
        {
            var key = new DocumentKey(input.Collection, input.Variant.Version, input.Variant.Locale, id);
            if (referenceMap.ContainsKey(key) || !allRoutes.TryAdd(key, RouteFor(input.Variant.RoutePrefix, id, context.Site.BaseUrl)))
                throw new ArgumentException("Duplicate API identity: " + key);
        }
        foreach (var entry in xml.Entries)
            AddRoute(entry.Input, entry.Id);
        foreach (var item in loaded.Where(item => item.OpenApi))
            foreach (var member in OpenApiReferenceReader.Read(item.Bytes))
            {
                AddRoute(item.Input, member.Id);
                allMembers.Add(new(item.Input.Collection, item.Input.Variant.Version, item.Input.Variant.Locale, member.Id), member);
            }
        SiteUrl? FindUrl(DocumentKey key) => allRoutes.TryGetValue(key, out var route) ? SiteUrl.FromRoute(route) : referenceMap.GetValueOrDefault(key);
        foreach (var (key, member) in xml.Read(FindUrl))
            allMembers.Add(key, member);
        static string LanguageName(string locale) => locale switch
        {
            "en" => "English",
            "ja" => "日本語",
            _ => locale,
        };
        static string LanguageNavigation(IEnumerable<(string Locale, SiteRoute Route)> available, string current)
        {
            var links = available.OrderBy(static variant => variant.Locale, StringComparer.Ordinal).ToArray();
            if (links.Length < 2)
                return string.Empty;
            return "<nav aria-label=\"" + Html.Encode(ApiXmlReferenceReader.Label(current, "API language")) + "\">"
                + string.Join(" ", links.Select(variant => "<a href=\"" + SiteUrl.FromRoute(variant.Route).ToAttributeValue()
                    + "\" hreflang=\"" + Html.Encode(variant.Locale) + "\" lang=\"" + Html.Encode(variant.Locale) + "\""
                    + (variant.Locale == current ? " aria-current=\"page\"" : string.Empty) + ">" + Html.Encode(LanguageName(variant.Locale)) + "</a>"))
                + "</nav>";
        }
        var collections = new List<SiteContentCollection>();
        foreach (var group in loaded.GroupBy(item => (item.Input.Collection, item.Input.Variant.Version, item.Input.Variant.Locale)))
        {
            var first = group.First();
            var input = first.Input;
            var variant = input.Variant;
            if (group.Any(item => (item.Input.Variant with { InputDirectory = variant.InputDirectory }) != variant))
                throw new ArgumentException("API inputs for one collection/version/language must use the same variant settings.");
            DocumentKey Key(string id) => new(input.Collection, variant.Version, variant.Locale, id);
            var parsed = allMembers.Where(pair => pair.Key.Collection == input.Collection && pair.Key.Version == variant.Version && pair.Key.Locale == variant.Locale)
                .Select(pair => pair.Value).OrderBy(member => member.Id, StringComparer.Ordinal).ToArray();
            // This dependency is repeated per page; retain the complete input identity without copying its symbol map.
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                string.Join(";", group.Select(item => item.Hash + ":" + JsonSerializer.Serialize(item.Source))))));
            var navigationHash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(allRoutes
                .Where(pair => pair.Key.Collection == input.Collection && pair.Key.Version == variant.Version)
                .OrderBy(static pair => pair.Key.Id, StringComparer.Ordinal).ThenBy(static pair => pair.Key.Locale, StringComparer.Ordinal)
                .Select(static pair => new { pair.Key.Id, pair.Key.Locale, pair.Value.PublicPath }))));
            var collection = new ContentCollection<ApiMember, ApiMember>(new($"api:{input.Collection}:{variant.Version}:{variant.Locale}"), Path.GetDirectoryName(Path.GetFullPath(input.FilePath))!,
                parsed.Select(member => new ContentEntry<ApiMember, ApiMember>(new(member.Id), Path.GetFileName(input.FilePath),
                    Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(member))), member, member)),
                entry => allRoutes[Key(entry.Id.Value)], entry => new(entry.FrontMatter.Title, entry.FrontMatter.Summary)
                { Document = Key(entry.Id.Value), Language = variant.Locale, RightToLeft = variant.RightToLeft, NoIndex = variant.NoIndex },
                declaredDependencies: [ContentDependency.FromValue("api.input", hash), ContentDependency.FromValue("api.variant", JsonSerializer.Serialize(variant)),
                    ContentDependency.FromValue("api.navigation", navigationHash), ContentDependency.FromValue("api.layout", layoutIdentity)], transformationId: new("api-v3"), isCacheable: true);
            collections.Add(new SiteContentCollection<ApiMember, ApiMember>(collection, (entry, rendering) => Render(rendering,
                LanguageNavigation(allRoutes.Where(pair => pair.Key.Collection == input.Collection && pair.Key.Version == variant.Version && pair.Key.Id == entry.Id.Value)
                    .Select(static pair => (pair.Key.Locale, pair.Value)), variant.Locale)
                + "<h1>" + Html.Encode(entry.FrontMatter.Title) + "</h1>" + (entry.FrontMatter.Deprecated ? "<aside role=\"note\">" + ApiXmlReferenceReader.Label(variant.Locale, "Deprecated") + "</aside>" : "") + entry.Body.BodyHtml, entry.Id.Value))
            { RendererFingerprint = "api-v3|" + layoutIdentity, IsThreadSafe = layout is null });
            collections.Add(collection.GeneratePages(new(collection.Id.Value + ":index"), _ => ["index"],
                entries => new SitePage<IReadOnlyList<ContentEntry<ApiMember, ApiMember>>>(new("index"), SiteRoute.ForDirectoryIndex(variant.RoutePrefix, context.Site.BaseUrl), entries.Entries,
                    new(input.Collection) { Language = variant.Locale, RightToLeft = variant.RightToLeft, NoIndex = variant.NoIndex }),
                (page, rendering) => Render(rendering, LanguageNavigation(loaded.Where(item => item.Input.Collection == input.Collection && item.Input.Variant.Version == variant.Version
                        && allMembers.Keys.Any(key => key.Collection == input.Collection && key.Version == variant.Version && key.Locale == item.Input.Variant.Locale))
                    .GroupBy(static item => item.Input.Variant.Locale, StringComparer.Ordinal)
                    .Select(group => (group.Key, SiteRoute.ForDirectoryIndex(group.First().Input.Variant.RoutePrefix, context.Site.BaseUrl))), variant.Locale)
                    + "<h1>" + Html.Encode(input.Collection) + "</h1><ul>" + string.Concat(page.Content.Select(entry =>
                    "<li><a href=\"" + SiteUrl.FromRoute(allRoutes[Key(entry.Id.Value)]).ToAttributeValue() + "\">" + Html.Encode(entry.FrontMatter.Title) + "</a></li>")) + "</ul>", page.Id.Value),
                transformationId: new("api-index-v3"), isCacheable: true));
        }
        foreach (var comparison in comparisons)
        {
            var before = allMembers.Where(pair => pair.Key.Collection == comparison.Collection && pair.Key.Version == comparison.Previous && pair.Key.Locale == comparison.Locale).Select(pair => pair.Value).ToArray();
            var after = allMembers.Where(pair => pair.Key.Collection == comparison.Collection && pair.Key.Version == comparison.Current && pair.Key.Locale == comparison.Locale).Select(pair => pair.Value).ToArray();
            if (!inputs.Any(value => value.Input.Collection == comparison.Collection && value.Input.Variant.Version == comparison.Previous && value.Input.Variant.Locale == comparison.Locale)
                || !inputs.Any(value => value.Input.Collection == comparison.Collection && value.Input.Variant.Version == comparison.Current && value.Input.Variant.Locale == comparison.Locale))
                throw new ArgumentException("API comparison requires both registered variants.");
            var changes = Compare(before, after);
            var title = ApiXmlReferenceReader.Label(comparison.Locale, "API changes");
            var body = "<h1>" + title + "</h1><ul>" + string.Concat(changes.Select(change => "<li><code>" + Html.Encode(change.Id) + "</code>: " + Html.Encode(ApiXmlReferenceReader.Label(comparison.Locale, change.Kind)) + "</li>")) + "</ul>";
            var collection = new ContentCollection<string, string>(new("api-diff:" + comparison.Collection + ":" + comparison.Previous + ":" + comparison.Current + ":" + comparison.Locale),
                context.CacheDirectory, [new ContentEntry<string, string>(new("changes"), "api-changes", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body))), title, body)],
                _ => SiteRoute.ForDirectoryIndex(comparison.Route, context.Site.BaseUrl), _ => new(title) { Language = comparison.Locale },
                declaredDependencies: [ContentDependency.FromValue("api.layout", layoutIdentity)], transformationId: new("api-diff-v2"), isCacheable: true);
            collections.Add(new SiteContentCollection<string, string>(collection, (entry, rendering) => Render(rendering, entry.Body, entry.Id.Value))
            { RendererFingerprint = "api-diff-v2|" + layoutIdentity, IsThreadSafe = layout is null });
        }
        members = allMembers;
        routes = allRoutes;
        return new() { ContentCollections = collections };
    }

}

/// <summary>Explicit execution for trusted API projects and runnable documentation examples.</summary>
public static partial class DocumentationVerification
{
    /// <summary>Builds XML documentation for a trusted csproj or solution and returns its generated XML files.</summary>
    public static async Task<IReadOnlyList<string>> BuildApiAsync(string projectOrSolution, CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(projectOrSolution);
        if (Path.GetExtension(path) is not (".csproj" or ".sln" or ".slnx"))
            throw new ArgumentException("A project or solution is required.");
        await RunAsync(path, ["build", path, "--no-restore", "-p:GenerateDocumentationFile=true"], cancellationToken).ConfigureAwait(false);
        return Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.xml", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .Where(file => file.Split(Path.DirectorySeparatorChar).Contains("bin") && File.Exists(Path.ChangeExtension(file, ".dll"))).Order(StringComparer.Ordinal).ToArray();
    }
    /// <summary>Runs the existing tests of a trusted project with no implicit dependency restore.</summary>
    public static Task VerifyExamplesAsync(string project, CancellationToken cancellationToken = default) => RunAsync(Path.GetFullPath(project), ["test", "--project", Path.GetFullPath(project), "--no-restore"], cancellationToken);
    private static async Task RunAsync(string path, string[] arguments, CancellationToken cancellationToken)
    {
        var result = await RunCommandAsync(path, arguments, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new IOException("Documentation verification failed with exit code " + result.ExitCode + ". Run the same project command directly for diagnostics.");
    }

    internal sealed record CommandResult(int ExitCode, string Output, string Error);

    internal static async Task<CommandResult> RunCommandAsync(string path, string[] arguments, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Documentation verification input does not exist.", path);
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(path)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Cannot start dotnet.");
        async Task<string> Drain(StreamReader reader)
        {
            var text = new System.Text.StringBuilder();
            var buffer = new char[4096];
            int count;
            while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                // Keep bounded failure evidence while draining the child's pipes fully.
                text.Append(buffer, 0, Math.Min(count, 16384 - text.Length));
            }
            return text.ToString();
        }
        var output = Drain(process.StandardOutput);
        var error = Drain(process.StandardError);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            await Task.WhenAll(output, error).ConfigureAwait(false);
        }
        return new CommandResult(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
    }
}
