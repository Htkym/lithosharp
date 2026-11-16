using LithoSharp.Build;
using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Documentation;

namespace LithoSharp.Tests;

public sealed class ApiReferenceSymbolTests
{
    [Test]
    public async Task ExplicitAssemblyMapsResolveInheritedDocumentationAndExactOverloads()
    {
        using var workspace = new TemporaryWorkspace();
        const string baseId = "M:Base.Box`1.Get(System.Int32)";
        const string methodId = "M:Library.Box`1.Get(System.Int32)";
        const string overloadId = "M:Library.Box`1.Get(System.String)";
        const string externalId = "T:System.String";
        var baseInput = await Xml(workspace, "base", "base", """
            <doc><members><member name="M:Base.Box`1.Get(System.Int32)"><summary>Reads <see cref="T:System.String"/> at <paramref name="originalIndex"/>.</summary><param name="originalIndex">The item index.</param><returns>The selected item.</returns><remarks>Inherited details.</remarks></member></members></doc>
            """);
        var input = await Xml(workspace, "library", "library", """
            <doc><members><member name="M:Library.Box`1.Get(System.Int32)"><inheritdoc/><remarks>Local details.</remarks></member></members></doc>
            """);
        var other = await Xml(workspace, "other", "library", """
            <doc><members><member name="M:Library.Box`1.Get(System.String)"><summary>Reads by name.</summary></member></members></doc>
            """);
        other = other with { Variant = other.Variant with { InputDirectory = Path.Combine(workspace.Root, "other-assembly") } };
        var api = new ApiReferenceSite();
        var external = new DocumentKey("framework", "10", "en", externalId);
        api.AddReferenceMap(new Dictionary<DocumentKey, SiteUrl> { [external] = SiteUrl.FromAbsolute("https://example.test/string") });
        api.AddXml(baseInput, new("Base", [new(baseId, "public virtual T Get(int originalIndex)", ApiVisibility.Public) { Parameters = [new("originalIndex", "int")] }], new Dictionary<string, DocumentKey> { [externalId] = external }));
        api.AddXml(input, new("Library", [new(methodId, "public override T Get(int index)", ApiVisibility.Public)
        {
            Parameters = [new("index", "int")], ReturnType = "T", GenericConstraints = ["where T : class"],
            Inheritance = [externalId], InheritDocId = baseId, SourcePath = "Box.cs"
        }], new Dictionary<string, DocumentKey> { [baseId] = new("base", "v1", "en", baseId), [externalId] = external }));
        api.AddXml(other, new("Other", [new(overloadId, "public T Get(string name)", ApiVisibility.Public)], new Dictionary<string, DocumentKey>()));
        var contribution = await api.PrepareAsync(Context(workspace));
        var member = api.Members[new("library", "v1", "en", methodId)];
        await Assert.That(member.AssemblyName).IsEqualTo("Library");
        await Assert.That(member.Symbol!.SourcePath).IsEqualTo("Box.cs");
        await Assert.That(member.BodyHtml).Contains("public override T Get(int index)");
        await Assert.That(member.BodyHtml).Contains("The item index.");
        await Assert.That(member.BodyHtml).Contains("The selected item.");
        await Assert.That(member.BodyHtml).Contains("at <code>index</code>");
        await Assert.That(member.BodyHtml).DoesNotContain("originalIndex");
        await Assert.That(member.BodyHtml.Split("The item index.", StringSplitOptions.None).Length).IsEqualTo(2);
        await Assert.That(member.BodyHtml).Contains("where T : class");
        await Assert.That(member.BodyHtml).Contains("https://example.test/string");
        await Assert.That(member.BodyHtml).Contains("Local details.");
        await Assert.That(member.BodyHtml).DoesNotContain("Inherited details.");
        await Assert.That(contribution.ContentCollections.Count).IsEqualTo(4);
        var inputIdentities = contribution.ContentCollections.OfType<SiteContentCollection<ApiMember, ApiMember>>()
            .SelectMany(collection => collection.Collection.DeclaredDependencies).Where(dependency => dependency.Key == "api.input").ToArray();
        await Assert.That(inputIdentities.Length > 0).IsTrue();
        await Assert.That(inputIdentities.All(dependency => dependency.Value!.Length <= 128)).IsTrue();
        await Assert.That(api.Resolve(new("library", "v1", "en", methodId)).Route.PublicPath
            == api.Resolve(new("library", "v1", "en", overloadId)).Route.PublicPath).IsFalse();
        var prepared = api.Members;
        api.AddComparison("library", "missing", "v1", "en", "changes");
        await Assert.That(async () => await api.PrepareAsync(Context(workspace))).Throws<ArgumentException>();
        await Assert.That(ReferenceEquals(prepared, api.Members)).IsTrue();
    }

    [Test]
    public async Task NestedInheritdocAndSymbolOnlyMembersKeepUnavailableInformationExplicit()
    {
        using var workspace = new TemporaryWorkspace();
        var input = await Xml(workspace, "nested", "library", """
            <doc><members><member name="T:Base"><summary>Base summary.</summary></member><member name="T:Child"><inheritdoc cref="T:Base"/><summary>Prefix <inheritdoc/></summary></member></members></doc>
            """);
        var api = new ApiReferenceSite();
        api.AddXml(input, new("Library", [new("T:Undocumented", "public class Undocumented", ApiVisibility.Public)], new Dictionary<string, DocumentKey>()));
        await api.PrepareAsync(Context(workspace));
        await Assert.That(api.Members[new("library", "v1", "en", "T:Child")].Summary).IsEqualTo("Prefix Base summary.");
        await Assert.That(api.Members[new("library", "v1", "en", "T:Base")].Symbol is null).IsTrue();
        await Assert.That(api.Members[new("library", "v1", "en", "T:Undocumented")].Summary).IsEqualTo("");
        await Assert.That(api.Members[new("library", "v1", "en", "T:Undocumented")].BodyHtml).Contains("public class Undocumented");
    }

    [Test]
    public async Task CyclicAndUnresolvedReferencesIdentifyTheMemberAndSource()
    {
        using var workspace = new TemporaryWorkspace();
        var cases = new[]
        {
            ("<member name=\"T:A\"><inheritdoc cref=\"T:B\"/></member><member name=\"T:B\"><inheritdoc cref=\"T:A\"/></member>", "Cyclic inheritdoc"),
            ("<member name=\"T:A\"><inheritdoc cref=\"T:Missing\"/></member>", "Unresolved inheritdoc"),
            ("<member name=\"T:A\"><summary><see cref=\"T:Missing\"/></summary></member>", "Unresolved API reference"),
            ("<member name=\"T:A\"><inheritdoc/></member>", "supply an exact cref"),
            ("<member name=\"T:A\"><inheritdoc cref=\"T:B\"/><inheritdoc cref=\"T:C\"/><summary><inheritdoc/></summary></member><member name=\"T:B\"><summary>B.</summary></member><member name=\"T:C\"><summary>C.</summary></member>", "Ambiguous inheritdoc")
        };
        foreach (var (xml, expected) in cases)
        {
            var input = await Xml(workspace, "invalid", "library", "<doc><members>" + xml + "</members></doc>");
            var api = new ApiReferenceSite();
            api.AddXml(input);
            ArgumentException? failure = null;
            try
            {
                await api.PrepareAsync(Context(workspace));
            }
            catch (ArgumentException error)
            {
                failure = error;
            }
            await Assert.That(failure is not null).IsTrue();
            await Assert.That(failure!.Message).Contains(expected);
            await Assert.That(failure.Message).Contains("T:A");
            await Assert.That(failure.Message).Contains(input.FilePath);
        }
    }

    [Test]
    public async Task DifferencesSeparateDocumentationFromSignatureVisibilityAndObsoleteInformation()
    {
        var before = new ApiMember("M:Box.Get", "Get", "Before", "<p>Before</p>")
        { Symbol = new("M:Box.Get", "public string Get()", ApiVisibility.Public) { ReturnType = "string" } };
        var documentation = before with { Summary = "After", BodyHtml = "<p>After</p>" };
        await Assert.That(ApiReferenceSite.Compare([before], [documentation]).Select(change => change.Kind)).IsEquivalentTo(new[] { "documentation" });
        var after = before with
        {
            Deprecated = true,
            Symbol = new("M:Box.Get", "protected object Get()", ApiVisibility.Protected) { ReturnType = "object", ObsoleteMessage = "Use Read." }
        };
        await Assert.That(ApiReferenceSite.Compare([before], [after]).Select(change => change.Kind)).IsEquivalentTo(new[] { "signature", "visibility", "deprecated" });
        await Assert.That(ApiReferenceSite.Compare([before], [before with { Symbol = null }]).Select(change => change.Kind)).IsEquivalentTo(new[] { "symbol-information" });
        var unreportedReturn = before with { Symbol = new("M:Box.Get", "public string Get()", ApiVisibility.Public) };
        await Assert.That(ApiReferenceSite.Compare([before], [unreportedReturn]).Select(change => change.Kind)).IsEquivalentTo(new[] { "symbol-information" });
        var originalValues = new ApiMember(before.Id, before.Title, before.Summary, before.BodyHtml, before.Deprecated);
        await Assert.That(originalValues == before).IsTrue();
        await Assert.That(originalValues.GetHashCode()).IsEqualTo(before.GetHashCode());
    }

    [Test]
    public async Task VersionedLinksDoNotProduceFalseDocumentationDifferencesAndObsoleteMessagesStaySeparate()
    {
        using var workspace = new TemporaryWorkspace();
        var input = await Xml(workspace, "versions", "library", """
            <doc><members><member name="T:Box"><summary>See <see cref="T:Item"/>.</summary><obsolete>Use Item.</obsolete></member><member name="T:Item"><summary>An item.</summary></member></members></doc>
            """);
        var api = new ApiReferenceSite();
        api.AddXml(input);
        api.AddXml(input with { Variant = input.Variant with { Version = "v2", RoutePrefix = "api/v2" } });
        await api.PrepareAsync(Context(workspace));
        var before = api.Members[new("library", "v1", "en", "T:Box")];
        var after = api.Members[new("library", "v2", "en", "T:Box")];
        await Assert.That(ApiReferenceSite.Compare([before], [after]).Count).IsEqualTo(0);
        await File.WriteAllTextAsync(input.FilePath, (await File.ReadAllTextAsync(input.FilePath)).Replace("Use Item.", "Use NewBox.", StringComparison.Ordinal));
        await api.PrepareAsync(Context(workspace));
        var changed = api.Members[new("library", "v1", "en", "T:Box")];
        await Assert.That(ApiReferenceSite.Compare([before], [changed]).Select(change => change.Kind)).IsEquivalentTo(new[] { "obsolete" });
        var edited = before with { BodyHtml = "<p>A corrected explanation.</p>" };
        await Assert.That(ApiReferenceSite.Compare([before], [edited]).Select(change => change.Kind)).IsEquivalentTo(new[] { "documentation" });
        var originalValues = new ApiMember(before.Id, before.Title, before.Summary, before.BodyHtml, before.Deprecated);
        await Assert.That(ApiReferenceSite.Compare([before], [originalValues]).Count).IsEqualTo(0);
    }

    [Test]
    public async Task LocalizedApiPagesSwitchOnlyToExistingIdentitiesAndRefreshCachedNavigation()
    {
        using var workspace = new TemporaryWorkspace();
        const string id = "M:Box.Read(System.Int32)";
        var input = await Xml(workspace, "english", "library", """
            <doc><members><member name="M:Box.Read(System.Int32)"><summary>Reads an item.</summary><param name="index">Item index.</param><returns>The item.</returns><remarks>More details.</remarks></member><member name="T:EnglishOnly"><summary>English only.</summary></member></members></doc>
            """);
        var source = new ApiAssemblySource("Library", [new(id, "public string Read(int index)", ApiVisibility.Public)
        {
            Parameters = [new("index", "int")], ReturnType = "string", GenericConstraints = ["where T : class"],
        }], new Dictionary<string, DocumentKey>());
        var api = new ApiReferenceSite();
        api.AddXml(input, source);
        var output = Path.Combine(workspace.Root, "site");
        var generator = new SiteGenerator();
        var options = new SiteGenerationOptions
        {
            Extensions = [api],
            BuildTimestamp = DateTimeOffset.UnixEpoch,
            BuildCacheDirectory = Path.Combine(workspace.Root, "build-cache"),
        };
        Task<SiteGenerationResult> Build() => generator.GenerateWithOptionsAsync(new SiteSettings { BaseUrl = "https://example.test/project/" },
            [], output, false, new() { Template = new DocsSiteTemplate() }, options, default);
        await Build();
        var english = api.Resolve(new("library", "v1", "en", id)).Route;
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, english.RelativeOutputPath))).DoesNotContain("hreflang=");

        var translated = await Xml(workspace, "japanese", "library", """
            <doc><members><member name="M:Box.Read(System.Int32)"><summary>項目を読み取ります。</summary><param name="index">項目の位置。</param><returns>対象の項目。</returns><remarks>補足の説明。</remarks></member></members></doc>
            """);
        api.AddXml(translated with { Variant = translated.Variant with { Locale = "ja", RoutePrefix = "api/ja" } }, source);
        var empty = await Xml(workspace, "empty", "library", "<doc><members/></doc>");
        api.AddXml(empty with { Variant = empty.Variant with { Locale = "fr", RoutePrefix = "api/fr" } });
        await Build();
        var japanese = api.Resolve(new("library", "v1", "ja", id)).Route;
        var html = await File.ReadAllTextAsync(Path.Combine(output, japanese.RelativeOutputPath));
        await Assert.That(html).Contains("<h2>シグネチャ</h2>");
        await Assert.That(html).Contains("<h2>パラメーター</h2>");
        await Assert.That(html).Contains("<h2>戻り値</h2>");
        await Assert.That(html).Contains("<h2>型パラメーターの制約</h2>");
        await Assert.That(html).Contains("<h2>補足</h2>");
        await Assert.That(html).Contains("本文へ移動");
        await Assert.That(html).DoesNotContain("Generated by ");
        await Assert.That(html).Contains("public string Read(int index)");
        await Assert.That(html).Contains("href=\"" + english.PublicPath + "\" hreflang=\"en\"");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, english.RelativeOutputPath)))
            .Contains("href=\"" + japanese.PublicPath + "\" hreflang=\"ja\"");
        var untranslated = api.Resolve(new("library", "v1", "en", "T:EnglishOnly")).Route;
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, untranslated.RelativeOutputPath))).DoesNotContain("hreflang=");
        var index = await File.ReadAllTextAsync(Path.Combine(output, "api/library/index.html"));
        await Assert.That(index).Contains("href=\"/project/api/ja/\" hreflang=\"ja\"");
        await Assert.That(index).DoesNotContain("/project/api/fr/");
        var japaneseIndex = await File.ReadAllTextAsync(Path.Combine(output, "api/ja/index.html"));
        await Assert.That(japaneseIndex).Contains("<html lang=\"ja\"");
        await Assert.That(japaneseIndex).Contains("aria-label=\"APIの言語\"");
    }

    [Test]
    public async Task CustomApiLayoutCoversAllPageKindsAndItsSettingsInvalidateWarmMembers()
    {
        using var workspace = new TemporaryWorkspace();
        var input = await Xml(workspace, "layout", "library", "<doc><members><member name=\"T:Box\"><summary>A box.</summary></member></members></doc>");
        var first = input with { Variant = input.Variant with { Locale = "ja", NoIndex = true } };
        var second = first with { Variant = first.Variant with { Version = "v2", RoutePrefix = "api/v2" } };
        ApiReferenceSite Create(IPageLayout<PageLayoutContent> layout, string? fingerprint,
            string sourcePath = "Box.cs", string referenceVersion = "v1")
        {
            var api = new ApiReferenceSite { Layout = layout, LayoutFingerprint = fingerprint };
            var source = new ApiAssemblySource("Library", [new("T:Box", "public class Box", ApiVisibility.Public) { SourcePath = sourcePath }],
                new Dictionary<string, DocumentKey> { ["T:Unused"] = new("external", referenceVersion, "en", "T:Unused") });
            api.AddXml(first, source);
            api.AddXml(second, source);
            api.AddComparison("library", "v1", "v2", "ja", "api/changes");
            return api;
        }
        await Assert.That(async () => await Create(new ProbeApiLayout("missing"), null).PrepareAsync(Context(workspace))).Throws<ArgumentException>();
        var layout = new ProbeApiLayout("first");
        var api = Create(layout, "configuration:first");
        var output = Path.Combine(workspace.Root, "site");
        var options = new SiteGenerationOptions
        {
            BuildTimestamp = DateTimeOffset.UnixEpoch,
            BuildCacheDirectory = Path.Combine(workspace.Root, "cache"),
            Extensions = [api],
        };
        var generator = new SiteGenerator();
        Task<SiteGenerationResult> Build(SiteGenerationOptions selected) => generator.GenerateWithOptionsAsync(
            new SiteSettings { BaseUrl = "https://example.test/" }, [], output, false, new() { Template = new DocsSiteTemplate() }, selected, default);
        await Build(options);
        await Assert.That(layout.Calls.Select(call => call.Id)).IsEquivalentTo(new[] { "T:Box", "T:Box", "index", "index", "changes" });
        await Assert.That(layout.Calls.All(call => call.Language == "ja")).IsTrue();
        await Assert.That(layout.Calls.Where(call => call.Id != "changes").All(call => call.NoIndex)).IsTrue();
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "api/changes/index.html"))).Contains("data-api-layout=\"first\"");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "api/library/index.html"))).Contains("data-api-layout=\"first\"");
        var route = api.Resolve(new("library", "v1", "ja", "T:Box")).Route;
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, route.RelativeOutputPath))).Contains("data-api-layout=\"first\"");

        var changedLayout = new ProbeApiLayout("second");
        var changed = Create(changedLayout, "configuration:second");
        await Build(options with { Extensions = [changed] });
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, route.RelativeOutputPath))).Contains("data-api-layout=\"second\"");
        await Assert.That(changedLayout.Calls.Any(call => call.Id == "T:Box")).IsTrue();

        var unchangedHtml = await File.ReadAllTextAsync(Path.Combine(output, route.RelativeOutputPath));
        changedLayout.Calls.Clear();
        await Build(options with { Extensions = [Create(changedLayout, "configuration:second")] });
        await Assert.That(changedLayout.Calls.Any(call => call.Id == "T:Box")).IsFalse();
        foreach (var (sourcePath, referenceVersion) in new[] { ("MovedBox.cs", "v1"), ("MovedBox.cs", "v2") })
        {
            changedLayout.Calls.Clear();
            await Build(options with { Extensions = [Create(changedLayout, "configuration:second", sourcePath, referenceVersion)] });
            await Assert.That(changedLayout.Calls.Any(call => call.Id == "T:Box")).IsTrue();
            await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, route.RelativeOutputPath))).IsEqualTo(unchangedHtml);
        }
    }

    private sealed class ProbeApiLayout(string label) : IPageLayout<PageLayoutContent>
    {
        internal List<(string Id, string? Language, bool NoIndex)> Calls { get; } = [];
        public IHtmlContent Render(LithoSharp.Pages.SitePage<PageLayoutContent> page, PageRenderingContext context)
        {
            Calls.Add((page.Id.Value, page.Metadata.Language, page.Metadata.NoIndex));
            return Html.UnsafeRaw("<!doctype html><html lang=\"" + Html.Encode(page.Metadata.Language!)
                + "\"><head><title>API</title></head><body data-api-layout=\"" + Html.Encode(label) + "\">"
                + page.Content.Body.ToHtmlString() + "</body></html>");
        }
    }

    private static async Task<ApiReferenceInput> Xml(TemporaryWorkspace workspace, string file, string collection, string xml)
    {
        var path = Path.Combine(workspace.Root, file + ".xml");
        await File.WriteAllTextAsync(path, xml);
        return new(collection, new("v1", "en", workspace.Root, "api/" + collection), path);
    }
    private static SiteBuildContext Context(TemporaryWorkspace workspace) => new(new SiteSettings { BaseUrl = "https://example.test/project/" }, new(),
        Path.Combine(workspace.Root, "out"), Path.Combine(workspace.Root, "cache"), DateTimeOffset.UnixEpoch);
}
