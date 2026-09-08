using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using LithoSharp.Build;
using LithoSharp.Content;
using LithoSharp.Pages;
using LithoSharp.Routing;

namespace LithoSharp.Documentation;

/// <summary>A documented API identity, including overload and generic XML IDs.</summary>
public sealed record ApiMember(string Id, string Title, string Summary, string BodyHtml, bool Deprecated = false);
/// <summary>A change between two explicitly selected API snapshots.</summary>
public sealed record ApiChange(string Id, string Kind);
/// <summary>An API input with the same identity and route dimensions as documentation.</summary>
public sealed record ApiReferenceInput(string Collection, DocumentVariant Variant, string FilePath);

/// <summary>XML comments and OpenAPI providers using typed pages and the common output transaction.</summary>
public sealed class ApiReferenceSite : ISiteBuildExtension
{
    private readonly List<(ApiReferenceInput Input, bool OpenApi)> inputs = [];
    private readonly List<(string Collection, string Previous, string Current, string Locale, string Route)> comparisons = [];
    /// <summary>Registers a derived comparison page between two explicitly loaded API variants.</summary>
    public void AddComparison(string collection, string previousVersion, string currentVersion, string locale, string routePrefix) => comparisons.Add((collection, previousVersion, currentVersion, locale, routePrefix));
    private IReadOnlyDictionary<DocumentKey, ApiMember> members = new Dictionary<DocumentKey, ApiMember>();
    private IReadOnlyDictionary<DocumentKey, SiteRoute> routes = new Dictionary<DocumentKey, SiteRoute>();
    /// <summary>The last prepared member catalog, keyed by collection, version, locale and exact API ID.</summary>
    public IReadOnlyDictionary<DocumentKey, ApiMember> Members => members;
    /// <summary>Registers XML documentation. Existing files are read; project code is not executed.</summary>
    public void AddXml(ApiReferenceInput input) => inputs.Add((input ?? throw new ArgumentNullException(nameof(input)), false));
    /// <summary>Registers OpenAPI 3 JSON. External references are diagnosed instead of fetched.</summary>
    public void AddOpenApi(ApiReferenceInput input) => inputs.Add((input ?? throw new ArgumentNullException(nameof(input)), true));
    /// <summary>Resolves an exact type, member, overload or generic ID in an explicit document variant.</summary>
    public PageRef<ApiMember> Resolve(DocumentKey key)
    {
        var route = routes.TryGetValue(key, out var found) ? found : throw new KeyNotFoundException("Missing API reference: " + key);
        var collection = new ContentCollectionId($"api:{key.Collection}:{key.Version}:{key.Locale}");
        return new(new(ContentPageIdentity.Create(collection, key.Id)), route);
    }
    /// <summary>Reports added, removed, changed and newly deprecated IDs for two snapshots.</summary>
    public static IReadOnlyList<ApiChange> Compare(IEnumerable<ApiMember> previous, IEnumerable<ApiMember> current)
    {
        var before = previous.ToDictionary(member => member.Id, StringComparer.Ordinal);
        var after = current.ToDictionary(member => member.Id, StringComparer.Ordinal);
        return before.Keys.Union(after.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(id =>
            !before.ContainsKey(id) ? new ApiChange(id, "added") : !after.ContainsKey(id) ? new(id, "removed") :
            !before[id].Deprecated && after[id].Deprecated ? new(id, "deprecated") : before[id] != after[id] ? new(id, "changed") : null).OfType<ApiChange>().ToArray();
    }
    /// <summary>A stable output route derived from the exact XML or OpenAPI identity.</summary>
    public static SiteRoute RouteFor(string prefix, string id, string? baseUrl = null) => SiteRoute.ForDirectoryIndex(prefix.Trim('/') + "/" +
        System.Text.RegularExpressions.Regex.Replace(id.Split('(')[0], @"[^\p{L}\p{N}_.-]", "-") + "-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..12], baseUrl);

    /// <inheritdoc />
    public async Task<SiteBuildContribution> PrepareAsync(SiteBuildContext context, CancellationToken cancellationToken = default)
    {
        var loaded = new List<(ApiReferenceInput Input, ApiMember[] Members, string Hash)>();
        var allMembers = new Dictionary<DocumentKey, ApiMember>();
        var allRoutes = new Dictionary<DocumentKey, SiteRoute>();
        foreach (var (input, openApi) in inputs)
        {
            var path = Path.GetFullPath(input.FilePath);
            var bytes = await ContentPath.ReadAllBytesAsync(Path.GetDirectoryName(path)!, path, cancellationToken).ConfigureAwait(false);
            var parsed = openApi ? ReadOpenApi(bytes) : ReadXml(bytes, input.Variant.RoutePrefix, context.Site.BaseUrl);
            loaded.Add((input, parsed, Convert.ToHexStringLower(SHA256.HashData(bytes))));
            foreach (var member in parsed)
            {
                var key = new DocumentKey(input.Collection, input.Variant.Version, input.Variant.Locale, member.Id);
                allMembers.Add(key, member);
                allRoutes.Add(key, RouteFor(input.Variant.RoutePrefix, member.Id, context.Site.BaseUrl));
            }
        }
        members = allMembers; routes = allRoutes;
        var collections = new List<SiteContentCollection>();
        foreach (var (input, parsed, hash) in loaded)
        {
            var variant = input.Variant;
            DocumentKey Key(string id) => new(input.Collection, variant.Version, variant.Locale, id);
            var collection = new ContentCollection<ApiMember, ApiMember>(new($"api:{input.Collection}:{variant.Version}:{variant.Locale}"), Path.GetDirectoryName(Path.GetFullPath(input.FilePath))!,
                parsed.Select(member => new ContentEntry<ApiMember, ApiMember>(new(member.Id), Path.GetFileName(input.FilePath),
                    Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(member))), member, member)),
                entry => allRoutes[Key(entry.Id.Value)], entry => new(entry.FrontMatter.Title, entry.FrontMatter.Summary)
                { Document = Key(entry.Id.Value), Language = variant.Locale, RightToLeft = variant.RightToLeft, NoIndex = variant.NoIndex },
                declaredDependencies: [ContentDependency.FromValue("api.input", hash), ContentDependency.FromValue("api.variant", JsonSerializer.Serialize(variant))], transformationId: new("api-v1"), isCacheable: true);
            collections.Add(new SiteContentCollection<ApiMember, ApiMember>(collection, (entry, rendering) => rendering.RenderDocument(
                "<h1>" + Html.Encode(entry.FrontMatter.Title) + "</h1>" + (entry.FrontMatter.Deprecated ? "<aside role=\"note\">Deprecated</aside>" : "") + entry.Body.BodyHtml)) { RendererFingerprint = "api-v1", IsThreadSafe = true });
            collections.Add(collection.GeneratePages(new(collection.Id.Value + ":index"), _ => ["index"],
                group => new SitePage<IReadOnlyList<ContentEntry<ApiMember, ApiMember>>>(new("index"), SiteRoute.ForDirectoryIndex(variant.RoutePrefix, context.Site.BaseUrl), group.Entries, new(input.Collection)),
                (page, rendering) => rendering.RenderDocument("<h1>" + Html.Encode(input.Collection) + "</h1><ul>" + string.Concat(page.Content.Select(entry =>
                    "<li><a href=\"" + SiteUrl.FromRoute(allRoutes[Key(entry.Id.Value)]).ToAttributeValue() + "\">" + Html.Encode(entry.FrontMatter.Title) + "</a></li>")) + "</ul>"),
                transformationId: new("api-index-v1"), isCacheable: true));
        }
        foreach (var comparison in comparisons)
        {
            var before = members.Where(pair => pair.Key.Collection == comparison.Collection && pair.Key.Version == comparison.Previous && pair.Key.Locale == comparison.Locale).Select(pair => pair.Value).ToArray();
            var after = members.Where(pair => pair.Key.Collection == comparison.Collection && pair.Key.Version == comparison.Current && pair.Key.Locale == comparison.Locale).Select(pair => pair.Value).ToArray();
            if (!inputs.Any(value => value.Input.Collection == comparison.Collection && value.Input.Variant.Version == comparison.Previous && value.Input.Variant.Locale == comparison.Locale)
                || !inputs.Any(value => value.Input.Collection == comparison.Collection && value.Input.Variant.Version == comparison.Current && value.Input.Variant.Locale == comparison.Locale)) throw new ArgumentException("API comparison requires both registered variants.");
            var changes = Compare(before, after);
            var body = "<h1>API changes</h1><ul>" + string.Concat(changes.Select(change => "<li><code>" + Html.Encode(change.Id) + "</code>: " + Html.Encode(change.Kind) + "</li>")) + "</ul>";
            var collection = new ContentCollection<string, string>(new("api-diff:" + comparison.Collection + ":" + comparison.Previous + ":" + comparison.Current + ":" + comparison.Locale),
                context.CacheDirectory, [new ContentEntry<string, string>(new("changes"), "api-changes", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body))), "API changes", body)],
                _ => SiteRoute.ForDirectoryIndex(comparison.Route, context.Site.BaseUrl), _ => new("API changes") { Language = comparison.Locale }, transformationId: new("api-diff-v1"), isCacheable: true);
            collections.Add(new SiteContentCollection<string, string>(collection, (entry, rendering) => rendering.RenderDocument(entry.Body)) { RendererFingerprint = "api-diff-v1" });
        }
        return new() { ContentCollections = collections };
    }

    private static ApiMember[] ReadXml(byte[] bytes, string prefix, string baseUrl)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var document = XDocument.Load(reader, LoadOptions.SetLineInfo);
        var elements = document.Root?.Element("members")?.Elements("member").ToArray() ?? throw new ArgumentException("XML documentation requires doc/members/member.");
        var ids = elements.Select(element => element.Attribute("name")?.Value ?? throw new ArgumentException("XML member requires a name.")).ToHashSet(StringComparer.Ordinal);
        string Render(XNode node) => node switch
        {
            XText text => Html.Encode(text.Value),
            XElement element when element.Name.LocalName is "see" or "seealso" => element.Attribute("cref") is { } reference
                ? ids.Contains(reference.Value) ? "<a href=\"" + SiteUrl.FromRoute(RouteFor(prefix, reference.Value, baseUrl)).ToAttributeValue() + "\">" + Html.Encode(element.Value.Length == 0 ? reference.Value : element.Value) + "</a>"
                    : throw new ArgumentException("Unresolved API cref: " + reference.Value)
                : Html.Encode(element.Value),
            XElement element when element.Name.LocalName == "para" => "<p>" + string.Concat(element.Nodes().Select(Render)) + "</p>",
            XElement element when element.Name.LocalName == "code" => "<pre><code>" + Html.Encode(element.Value) + "</code></pre>",
            XElement element when element.Name.LocalName is "c" or "paramref" or "typeparamref" => "<code>" + Html.Encode(element.Attribute("name")?.Value ?? element.Value) + "</code>",
            XElement element => string.Concat(element.Nodes().Select(Render)),
            _ => ""
        };
        return elements.Select(element =>
        {
            var id = element.Attribute("name")!.Value;
            var summary = element.Element("summary")?.Value.Trim() ?? "";
            var body = "<p>" + string.Concat(element.Element("summary")?.Nodes().Select(Render) ?? []) + "</p>";
            foreach (var section in element.Elements().Where(section => section.Name.LocalName != "summary"))
                body += "<section><h2>" + Html.Encode(section.Name.LocalName + (section.Attribute("name") is { } name ? " " + name.Value : "")) + "</h2>" + Render(section) + "</section>";
            return new ApiMember(id, id.Length > 2 ? id[2..] : id, summary, body, element.Element("deprecated") is not null || element.Element("obsolete") is not null);
        }).ToArray();
    }
    private static ApiMember[] ReadOpenApi(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (!root.TryGetProperty("openapi", out var version) || !version.GetString()!.StartsWith("3.", StringComparison.Ordinal)) throw new ArgumentException("OpenAPI 3 JSON is required.");
        void Validate(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name)) throw new ArgumentException("Duplicate OpenAPI key: " + property.Name);
                    if (property.Name == "$ref" && !property.Value.GetString()!.StartsWith("#/", StringComparison.Ordinal)) throw new ArgumentException("External OpenAPI references must be bundled explicitly before generation.");
                    Validate(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) Validate(item);
        }
        Validate(root);
        var result = new List<ApiMember>();
        foreach (var path in root.GetProperty("paths").EnumerateObject())
        foreach (var operation in path.Value.EnumerateObject().Where(property => property.Name is "get" or "put" or "post" or "delete" or "options" or "head" or "patch" or "trace"))
        {
            var value = operation.Value;
            var id = "O:" + operation.Name.ToUpperInvariant() + " " + path.Name;
            var title = value.TryGetProperty("summary", out var summary) ? summary.GetString()! : id[2..];
            var description = value.TryGetProperty("description", out var detail) ? detail.GetString()! : "";
            result.Add(new(id, title, description, "<p><code>" + Html.Encode(id[2..]) + "</code></p><p>" + Html.Encode(description) + "</p><pre><code>" + Html.Encode(JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true })) + "</code></pre>",
                value.TryGetProperty("deprecated", out var deprecated) && deprecated.GetBoolean()));
        }
        return result.ToArray();
    }
}

/// <summary>Explicit execution for trusted API projects and runnable documentation examples.</summary>
public static class DocumentationVerification
{
    /// <summary>Builds XML documentation for a trusted csproj or solution and returns its generated XML files.</summary>
    public static async Task<IReadOnlyList<string>> BuildApiAsync(string projectOrSolution, CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(projectOrSolution);
        if (Path.GetExtension(path) is not (".csproj" or ".sln" or ".slnx")) throw new ArgumentException("A project or solution is required.");
        await RunAsync(path, ["build", path, "--no-restore", "-p:GenerateDocumentationFile=true"], cancellationToken).ConfigureAwait(false);
        return Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.xml", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .Where(file => file.Split(Path.DirectorySeparatorChar).Contains("bin") && File.Exists(Path.ChangeExtension(file, ".dll"))).Order(StringComparer.Ordinal).ToArray();
    }
    /// <summary>Runs the existing tests of a trusted project with no implicit dependency restore.</summary>
    public static Task VerifyExamplesAsync(string project, CancellationToken cancellationToken = default) => RunAsync(Path.GetFullPath(project), ["test", "--project", Path.GetFullPath(project), "--no-restore"], cancellationToken);
    private static async Task RunAsync(string path, string[] arguments, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Documentation verification input does not exist.", path);
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = Path.GetDirectoryName(path)!, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Cannot start dotnet.");
        // Drain output without retaining an unbounded log from executed project code.
        async Task Drain(StreamReader reader) { var buffer = new char[4096]; while (await reader.ReadAsync(buffer).ConfigureAwait(false) > 0) { } }
        var output = Drain(process.StandardOutput); var error = Drain(process.StandardError);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); throw; }
        finally { await Task.WhenAll(output, error).ConfigureAwait(false); }
        if (process.ExitCode != 0) throw new IOException("Documentation verification failed with exit code " + process.ExitCode + ". Run the same project command directly for diagnostics.");
    }
}
