using LithoSharp.Content;
using LithoSharp.Diagnostics;
using LithoSharp.Pages;
using LithoSharp.Routing;
using System.Text.Json;
using YamlDotNet.Serialization;

namespace LithoSharp.Tests;

public sealed class StructuredContentLoaderTests
{
    [Test]
    public async Task JsonLoader_LoadsObjectsAndArraysInDeterministicOrder()
    {
        using var workspace = new TemporaryWorkspace();
        Directory.CreateDirectory(Path.Combine(workspace.Root, "z"));
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "z", "items.json"),
            """[{"id":"b","title":"B"},{"id":"a","title":"A"}]""");
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "item.json"),
            """{"id":"c","title":"C"}""");

        var loader = new JsonContentCollectionLoader<Row, string>(
            workspace.Root, new ContentCollectionId("items"), new RowBinder(),
            element => element.GetProperty("title").GetString()!,
            static entry => SiteRoute.ForFile(entry.Id.Value + ".html"),
            static entry => new PageMetadata(entry.FrontMatter.Title));

        var result = await loader.LoadAsync();

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Collection!.Entries.Select(static entry => entry.Id.Value)
            .SequenceEqual(["a", "b", "c"])).IsTrue();
        await Assert.That(result.Collection.Entries[0].SourceFingerprint).StartsWith("sha256:");
    }

    [Test]
    public async Task YamlLoader_LoadsObjectsAndSequencesWithNestedValues()
    {
        using var workspace = new TemporaryWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "items.YML"), """
            - id: b
              title: B
              nested:
                name: N
                values: [1, 2]
            - id: a
              title: A
            """);
        var loader = new YamlContentCollectionLoader<Row, IReadOnlyDictionary<string, object?>>(
            workspace.Root, new ContentCollectionId("items"), new RowBinder(),
            static values => values,
            static entry => SiteRoute.ForFile(entry.Id.Value + ".html"),
            static entry => new PageMetadata(entry.FrontMatter.Title));

        var result = await loader.LoadAsync();

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Collection!.Entries.Select(static entry => entry.Id.Value)
            .SequenceEqual(["a", "b"])).IsTrue();
        await Assert.That(((IReadOnlyDictionary<string, object?>)result.Collection.Entries[1].Body["nested"]!)
            ["name"]).IsEqualTo("N");
        await Assert.That(result.Collection.Entries[0].SourceFingerprint).StartsWith("sha256:");
    }

    [Test]
    public async Task YamlLoader_ReportsStrictYamlDiagnosticsAndDuplicateIds()
    {
        using var workspace = new TemporaryWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "a.yaml"), """
            id: same
            title: A
            """);
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "b.yaml"), """
            id: same
            title: B
            duplicate: 1
            duplicate: 2
            """);

        var loader = new YamlContentCollectionLoader<Row, string>(
            workspace.Root, new ContentCollectionId("items"), new RowBinder(),
            static values => (string)values["title"]!,
            static entry => SiteRoute.ForFile(entry.Id.Value + ".html"),
            static entry => new PageMetadata(entry.FrontMatter.Title));
        var result = await loader.LoadAsync();

        await Assert.That(result.IsSuccess).IsFalse();
        await Assert.That(result.Diagnostics.Any(static diagnostic =>
            diagnostic.Id == YamlContentDiagnosticIds.DuplicateKey)).IsTrue();
    }

    [Test]
    public async Task YamlLoader_ReportsNestedNormalizedDuplicateKeyAtSecondKey()
    {
        using var workspace = new TemporaryWorkspace();
        var binderCalled = false;
        var bodyFactoryCalled = false;
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "item.yaml"), """
            id: item
            title: Item
            nested:
              café: first
              café: second
            """);

        var result = await new YamlContentCollectionLoader<Row, string>(
            workspace.Root,
            new ContentCollectionId("items"),
            new CallbackBinder<Row>(
                () => binderCalled = true,
                new Row("item", "Item")),
            values =>
            {
                bodyFactoryCalled = true;
                return (string)values["title"]!;
            },
            static entry => SiteRoute.ForFile(entry.Id.Value + ".html"),
            static entry => new PageMetadata(entry.FrontMatter.Title))
            .LoadAsync();

        await Assert.That(result.IsSuccess).IsFalse();
        var diagnostic = result.Diagnostics.Single();
        await Assert.That(diagnostic.Id).IsEqualTo(YamlContentDiagnosticIds.DuplicateKey);
        await Assert.That(diagnostic.Location!.FilePath).IsEqualTo("item.yaml");
        await Assert.That(diagnostic.Location.Line).IsEqualTo(5);
        await Assert.That(diagnostic.Location.Column).IsEqualTo(3);
        await Assert.That(binderCalled).IsFalse();
        await Assert.That(bodyFactoryCalled).IsFalse();
    }

    [Test]
    public async Task YamlLoader_PreservesReflectionBinderFieldLocationsAtNestedDepth()
    {
        using var workspace = new TemporaryWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "item.yaml"), """
            id: item
            title: Item
            details:
              label: null
              count: nope
              extra: rejected
            """);

        var result = await new YamlContentCollectionLoader<LocatedFrontMatter, string>(
            workspace.Root,
            new ContentCollectionId("items"),
            new ReflectionContentFrontMatterBinder<LocatedFrontMatter>(),
            static values => (string)values["title"]!,
            static entry => SiteRoute.ForFile(entry.Id.Value + ".html"),
            static entry => new PageMetadata(entry.FrontMatter.Title))
            .LoadAsync();

        await Assert.That(result.IsSuccess).IsFalse();
        var nullDiagnostic = result.Diagnostics.Single(static diagnostic =>
            diagnostic.Id == ContentFrontMatterDiagnosticIds.NullNotAllowed);
        var conversionDiagnostic = result.Diagnostics.Single(static diagnostic =>
            diagnostic.Id == ContentFrontMatterDiagnosticIds.InvalidValue);
        var unknownDiagnostic = result.Diagnostics.Single(static diagnostic =>
            diagnostic.Id == ContentFrontMatterDiagnosticIds.UnknownField);
        await Assert.That(nullDiagnostic.Location!.FilePath).IsEqualTo("item.yaml");
        await Assert.That(nullDiagnostic.Location.Line).IsEqualTo(4);
        await Assert.That(nullDiagnostic.Location.Column).IsEqualTo(10);
        await Assert.That(conversionDiagnostic.Location!.FilePath).IsEqualTo("item.yaml");
        await Assert.That(conversionDiagnostic.Location.Line).IsEqualTo(5);
        await Assert.That(conversionDiagnostic.Location.Column).IsEqualTo(10);
        await Assert.That(unknownDiagnostic.Location!.FilePath).IsEqualTo("item.yaml");
        await Assert.That(unknownDiagnostic.Location.Line).IsEqualTo(6);
        await Assert.That(unknownDiagnostic.Location.Column).IsEqualTo(3);
    }

    [Test]
    public async Task YamlLoader_NormalizesKeysCoherentlyForBinderAndBodyFactory()
    {
        using var workspace = new TemporaryWorkspace();
        IReadOnlyDictionary<string, object?>? bodyValues = null;
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "item.yaml"), """
            id: item
            café: normalized
            """);

        var result = await new YamlContentCollectionLoader<NormalizedAliasFrontMatter, string>(
            workspace.Root,
            new ContentCollectionId("items"),
            new ReflectionContentFrontMatterBinder<NormalizedAliasFrontMatter>(),
            values =>
            {
                bodyValues = values;
                return (string)values["café"]!;
            },
            static entry => SiteRoute.ForFile(entry.Id.Value + ".html"),
            static entry => new PageMetadata(entry.FrontMatter.Cafe))
            .LoadAsync();

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Collection!.Entries.Single().FrontMatter.Cafe)
            .IsEqualTo("normalized");
        await Assert.That(result.Collection.Entries.Single().Body).IsEqualTo("normalized");
        await Assert.That(bodyValues!.ContainsKey("café")).IsTrue();
        await Assert.That(bodyValues.ContainsKey("café")).IsFalse();
    }

    [Test]
    public async Task AllLoaders_ReportNormalizedRelativePathCollisions()
    {
        using var workspace = new TemporaryWorkspace();
        foreach (var extension in new[] { ".md", ".json", ".csv", ".yaml" })
        {
            await File.WriteAllTextAsync(Path.Combine(workspace.Root, $"café{extension}"), "");
            await File.WriteAllTextAsync(Path.Combine(workspace.Root, $"café{extension}"), "");
        }

        if (Directory.GetFiles(workspace.Root).Length != 8)
        {
            return;
        }

        var diagnostics = await LoadAllDiagnosticsAsync(workspace.Root);

        foreach (var loaderDiagnostics in diagnostics)
        {
            await Assert.That(loaderDiagnostics.Value.Count(static diagnostic =>
                diagnostic.Id == "LSC0007")).IsEqualTo(2);
        }
    }

    [Test]
    public async Task AllLoaders_ReportDirectorySymlinkOutsideInputRoot()
    {
        using var workspace = new TemporaryWorkspace();
        var inputRoot = Path.Combine(workspace.Root, "input");
        var outside = Path.Combine(workspace.Root, "outside");
        Directory.CreateDirectory(inputRoot);
        Directory.CreateDirectory(outside);
        if (!TryCreateDirectorySymbolicLink(Path.Combine(inputRoot, "linked"), outside))
        {
            return;
        }

        var diagnostics = await LoadAllDiagnosticsAsync(inputRoot);

        foreach (var loaderDiagnostics in diagnostics)
        {
            var diagnostic = loaderDiagnostics.Value.Single(static diagnostic =>
                diagnostic.Id == "LSC0008");
            await Assert.That(diagnostic.Location!.FilePath).IsEqualTo("linked");
        }
    }

    [Test]
    public async Task AllLoaders_ReportFileSymlinks()
    {
        using var workspace = new TemporaryWorkspace();
        var inputRoot = Path.Combine(workspace.Root, "input");
        var outside = Path.Combine(workspace.Root, "outside");
        Directory.CreateDirectory(inputRoot);
        Directory.CreateDirectory(outside);
        foreach (var extension in new[] { ".md", ".json", ".csv", ".yaml" })
        {
            var target = Path.Combine(outside, $"target{extension}");
            await File.WriteAllTextAsync(target, "");
            if (!TryCreateFileSymbolicLink(
                    Path.Combine(inputRoot, $"linked{extension}"),
                    target))
            {
                return;
            }
        }

        var diagnostics = await LoadAllDiagnosticsAsync(inputRoot);

        foreach (var loaderDiagnostics in diagnostics)
        {
            var diagnostic = loaderDiagnostics.Value.Single(static diagnostic =>
                diagnostic.Id == "LSC0008");
            await Assert.That(diagnostic.Location!.FilePath)
                .IsEqualTo($"linked{loaderDiagnostics.Key}");
        }
    }

    [Test]
    public async Task StructuredLoaders_DiscoverUppercaseExtensions()
    {
        using var workspace = new TemporaryWorkspace();
        var jsonRoot = Path.Combine(workspace.Root, "json");
        var csvRoot = Path.Combine(workspace.Root, "csv");
        Directory.CreateDirectory(jsonRoot);
        Directory.CreateDirectory(csvRoot);
        await File.WriteAllTextAsync(
            Path.Combine(jsonRoot, "ITEM.JSON"),
            """{"id":"json","title":"JSON"}""");
        await File.WriteAllTextAsync(
            Path.Combine(csvRoot, "ITEM.CSV"),
            "id,title\r\ncsv,CSV\r\n");

        var json = await new JsonContentCollectionLoader<Row, string>(
            jsonRoot, new ContentCollectionId("json"), new RowBinder(),
            element => element.GetProperty("title").GetString()!,
            static entry => SiteRoute.ForFile(entry.Id.Value + ".html"),
            static entry => new PageMetadata(entry.FrontMatter.Title)).LoadAsync();
        var csv = await CreateCsvLoader(csvRoot).LoadAsync();

        await Assert.That(json.IsSuccess).IsTrue();
        await Assert.That(json.Collection!.Entries.Single().SourcePath).IsEqualTo("ITEM.JSON");
        await Assert.That(csv.IsSuccess).IsTrue();
        await Assert.That(csv.Collection!.Entries.Single().SourcePath).IsEqualTo("ITEM.CSV");
    }

    [Test]
    public async Task CsvLoader_ReportsMalformedQuotesAndDuplicateHeaders()
    {
        using var workspace = new TemporaryWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "bad.csv"),
            "id,id,title\r\n1,2,\"broken\"x\r\n");

        var loader = CreateCsvLoader(workspace.Root);
        var result = await loader.LoadAsync();

        await Assert.That(result.IsSuccess).IsFalse();
        await Assert.That(result.Diagnostics.Select(static diagnostic => diagnostic.Id)
            .Contains("LSCCSV003")).IsTrue();
        await Assert.That(result.Diagnostics.Select(static diagnostic => diagnostic.Id)
            .Contains("LSCCSV005")).IsTrue();
    }

    [Test]
    public async Task JsonLoader_ReportsDuplicateIdsAcrossFiles()
    {
        using var workspace = new TemporaryWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "a.json"), """{"id":"same","title":"A"}""");
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "b.json"), """{"id":"same","title":"B"}""");

        var loader = new JsonContentCollectionLoader<Row, string>(
            workspace.Root, new ContentCollectionId("items"), new RowBinder(),
            element => element.GetProperty("title").GetString()!,
            static entry => SiteRoute.ForFile(entry.Id.Value + ".html"),
            static entry => new PageMetadata(entry.FrontMatter.Title));
        var result = await loader.LoadAsync();

        await Assert.That(result.IsSuccess).IsFalse();
        await Assert.That(result.Diagnostics.Any(static diagnostic => diagnostic.Id == "LSC0006")).IsTrue();
    }

    [Test]
    public async Task Loader_HonorsCancellation()
    {
        using var workspace = new TemporaryWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "item.json"), """{"id":"a","title":"A"}""");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var loader = new JsonContentCollectionLoader<Row, string>(
            workspace.Root, new ContentCollectionId("items"), new RowBinder(),
            element => element.GetProperty("title").GetString()!,
            static entry => SiteRoute.ForFile(entry.Id.Value + ".html"),
            static entry => new PageMetadata(entry.FrontMatter.Title));

        await Assert.That(async () => await loader.LoadAsync(cancellation.Token))
            .Throws<OperationCanceledException>();
    }

    [Test]
    public async Task Cancellation_IsObservedForEmptyDirectories()
    {
        using var workspace = new TemporaryWorkspace();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var loader = new JsonContentCollectionLoader<Row, string>(
            workspace.Root, new ContentCollectionId("items"), new RowBinder(),
            element => element.GetProperty("title").GetString()!,
            static entry => SiteRoute.ForFile(entry.Id.Value + ".html"),
            static entry => new PageMetadata(entry.FrontMatter.Title));

        await Assert.That(async () => await loader.LoadAsync(cancellation.Token))
            .Throws<OperationCanceledException>();
    }

    [Test]
    public async Task JsonLoader_ClonesBodyAndMaterializesNestedValues()
    {
        using var workspace = new TemporaryWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "item.json"),
            """{"id":"a","title":"A","nested":{"name":"N","values":[1,2]}}""");
        JsonElement body = default;
        var loader = new JsonContentCollectionLoader<NestedRow, JsonElement>(
            workspace.Root, new ContentCollectionId("items"), new NestedBinder(),
            element => { body = element; return element; },
            static entry => SiteRoute.ForFile(entry.Id.Value + ".html"),
            static entry => new PageMetadata(entry.FrontMatter.Title));

        var result = await loader.LoadAsync();

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(body.GetProperty("nested").GetProperty("name").GetString()).IsEqualTo("N");
        await Assert.That(result.Collection!.Entries[0].FrontMatter.Nested!.Name).IsEqualTo("N");
    }

    [Test]
    public async Task CsvLoader_ReportsInvalidUtf8()
    {
        using var workspace = new TemporaryWorkspace();
        await File.WriteAllBytesAsync(Path.Combine(workspace.Root, "invalid.csv"), [0x69, 0x64, 0x0A, 0xC3, 0x28]);

        var result = await CreateCsvLoader(workspace.Root).LoadAsync();

        await Assert.That(result.Diagnostics.Any(static diagnostic => diagnostic.Id == "LSCCSV007")).IsTrue();
        await Assert.That(result.Diagnostics.Single(static diagnostic => diagnostic.Id == "LSCCSV007").Location!.Column)
            .IsEqualTo(4);
    }

    private static CsvContentCollectionLoader<Row, string> CreateCsvLoader(string root) =>
        new(root, new ContentCollectionId("items"), new RowBinder(),
            values => (string)values["title"]!,
            static entry => SiteRoute.ForFile(entry.Id.Value + ".html"),
            static entry => new PageMetadata(entry.FrontMatter.Title));

    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<SiteDiagnostic>>>
        LoadAllDiagnosticsAsync(string root)
    {
        var markdown = await new MarkdownContentCollectionLoader<DiscoveryFrontMatter>(
            new ContentCollectionId("markdown"),
            root,
            static entry => SiteRoute.ForFile(Path.ChangeExtension(entry.SourcePath, ".html")),
            static entry => new PageMetadata(entry.FrontMatter.Title))
            .LoadAsync();
        var json = await new JsonContentCollectionLoader<Row, string>(
            root,
            new ContentCollectionId("json"),
            new RowBinder(),
            static element => element.GetRawText(),
            static entry => SiteRoute.ForFile(entry.Id.Value + ".html"),
            static entry => new PageMetadata(entry.FrontMatter.Title))
            .LoadAsync();
        var csv = await CreateCsvLoader(root).LoadAsync();
        var yaml = await new YamlContentCollectionLoader<Row, string>(
            root,
            new ContentCollectionId("yaml"),
            new RowBinder(),
            static values => values.ToString()!,
            static entry => SiteRoute.ForFile(entry.Id.Value + ".html"),
            static entry => new PageMetadata(entry.FrontMatter.Title))
            .LoadAsync();

        return new Dictionary<string, IReadOnlyList<SiteDiagnostic>>(StringComparer.Ordinal)
        {
            [".md"] = markdown.Diagnostics,
            [".json"] = json.Diagnostics,
            [".csv"] = csv.Diagnostics,
            [".yaml"] = yaml.Diagnostics,
        };
    }

    private static bool TryCreateDirectorySymbolicLink(string path, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(path, target);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static bool TryCreateFileSymbolicLink(string path, string target)
    {
        try
        {
            File.CreateSymbolicLink(path, target);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }

    private sealed record Row(string Id, string Title);
    private sealed record NestedRow(string Id, string Title, NestedValue? Nested);
    private sealed record NestedValue(string Name, List<int> Values);

    private sealed class DiscoveryFrontMatter
    {
        public string Title { get; init; } = string.Empty;
    }

    private sealed class LocatedFrontMatter
    {
        public string Id { get; init; } = string.Empty;

        public string Title { get; init; } = string.Empty;

        public LocatedDetails Details { get; init; } = new();
    }

    private sealed class LocatedDetails
    {
        public string Label { get; init; } = string.Empty;

        public int Count { get; init; }
    }

    private sealed class NormalizedAliasFrontMatter
    {
        public string Id { get; init; } = string.Empty;

        [YamlMember(Alias = "café")]
        public string Cafe { get; init; } = string.Empty;
    }

    private sealed class CallbackBinder<T>(
        Action callback,
        T value) : IContentFrontMatterBinder<T>
        where T : notnull
    {
        public ContentParseResult<T> Bind(
            IReadOnlyDictionary<string, object?> values,
            SiteSourceLocation? sourceLocation = null)
        {
            callback();
            return ContentParseResult<T>.Success(value);
        }
    }

    private sealed class RowBinder : IContentFrontMatterBinder<Row>
    {
        public ContentParseResult<Row> Bind(
            IReadOnlyDictionary<string, object?> values,
            LithoSharp.Diagnostics.SiteSourceLocation? sourceLocation = null)
        {
            if (!values.TryGetValue("id", out var id) || id is not string idText || idText.Length == 0
                || !values.TryGetValue("title", out var title) || title is not string titleText)
            {
                return ContentParseResult<Row>.Failure([
                    new LithoSharp.Diagnostics.SiteDiagnostic(
                        "LSCVAL001", LithoSharp.Diagnostics.SiteDiagnosticSeverity.Error,
                        "id/title が必要です。", sourceLocation)]);
            }

            return ContentParseResult<Row>.Success(new Row(idText, titleText));
        }

    }

    private sealed class NestedBinder : IContentFrontMatterBinder<NestedRow>
    {
        public ContentParseResult<NestedRow> Bind(
            IReadOnlyDictionary<string, object?> values,
            LithoSharp.Diagnostics.SiteSourceLocation? sourceLocation = null)
        {
            var nested = (IReadOnlyDictionary<string, object?>)values["nested"]!;
            var valuesList = ((IEnumerable<object?>)nested["values"]!).Select(static value => Convert.ToInt32(value)).ToList();
            return ContentParseResult<NestedRow>.Success(new NestedRow(
                (string)values["id"]!, (string)values["title"]!,
                new NestedValue((string)nested["name"]!, valuesList)));
        }
    }
}
