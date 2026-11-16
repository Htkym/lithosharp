using System.Text.Json;
using LithoSharp.Configuration;
using LithoSharp.Documentation;
using LithoSharp.Search;

namespace LithoSharp.Tests;

public sealed class LocalizedSearchTests
{
    [Test]
    public async Task ApiIndexesRetainPageLanguageWithoutInventingDocumentIdentity()
    {
        using var workspace = new TemporaryWorkspace();
        var xml = Path.Combine(workspace.Root, "api.xml");
        await File.WriteAllTextAsync(xml, "<doc><members><member name=\"T:Example.PngCodec\"><summary>PNG codec.</summary></member></members></doc>");
        var api = new ApiReferenceSite();
        foreach (var locale in new[] { "en", "ja" })
            api.AddXml(new("images", new("v1", locale, workspace.Root, "api/" + locale + "/images"), xml));
        var output = Path.Combine(workspace.Root, "output");
        await new SiteGenerator().GenerateWithOptionsAsync(new SiteSettings { BaseUrl = "https://example.test/project/" }, [], output, false,
            new() { Template = new DocsSiteTemplate { EnableSearch = true } }, new() { Extensions = [api] }, default);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "search-index.json")));
        var documents = json.RootElement.GetProperty("documents").Deserialize<SearchDocument[]>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        foreach (var locale in new[] { "en", "ja" })
        {
            var index = documents.Single(document => document.Url == "/project/api/" + locale + "/images/");
            await Assert.That(index.Locale).IsEqualTo(locale);
            await Assert.That(index.Collection is null).IsTrue();
            await Assert.That(index.Version is null).IsTrue();
            var symbol = documents.Single(document => document.Url.StartsWith(index.Url, StringComparison.Ordinal) && document.Url != index.Url);
            await Assert.That(symbol.Collection).IsEqualTo("images");
            await Assert.That(symbol.Version).IsEqualTo("v1");
            await Assert.That(symbol.Locale).IsEqualTo(locale);
        }
        var results = LocalSearch.Query(documents, "PngCodec", locale: "ja");
        await Assert.That(results.Count).IsEqualTo(2);
        await Assert.That(results.All(hit => hit.Url.Contains("/api/ja/", StringComparison.Ordinal))).IsTrue();
    }
}
