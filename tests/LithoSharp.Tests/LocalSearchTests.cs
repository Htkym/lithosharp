using LithoSharp.Configuration;
using System.Text.Json;
using LithoSharp.Search;

namespace LithoSharp.Tests;

public sealed class LocalSearchTests
{
    [Test]
    public async Task GeneratedSearchIncludesReusableAutocompleteWithoutReplacingSearchPage()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "site");
        await new SiteGenerator().GenerateWithOptionsAsync(
            new SiteSettings { BaseUrl = "https://example.test/docs/" }, [], output, false,
            new() { Template = new DocsSiteTemplate { EnableSearch = true } },
            new SiteGenerationOptions(), CancellationToken.None);
        var script = await File.ReadAllTextAsync(Path.Combine(output, "assets", "search.js"));
        await Assert.That(script.Contains("[data-site-autocomplete]", StringComparison.Ordinal)).IsTrue();
        await Assert.That(script.Contains("const queryDocuments", StringComparison.Ordinal)).IsTrue();
        await Assert.That(script.Contains("getElementById(\"search-app\")", StringComparison.Ordinal)).IsTrue();
        await Assert.That(script.Contains("request !== requestId", StringComparison.Ordinal)).IsTrue();
        await Assert.That(script.Contains("url.origin !== location.origin", StringComparison.Ordinal)).IsTrue();
        await Assert.That(script.Contains("/*__LITHOSHARP_LOCAL_SEARCH__*/", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task ExcerptsKeepWordAndGraphemeBoundariesWithinBudget()
    {
        static string Excerpt(string body) => LocalSearch.Query([new SearchDocument("target", "", [], "/test", "", body)], "target").Single().Excerpt;
        const string markup = "<script>target & quoted</script>";
        await Assert.That(Excerpt(markup)).IsEqualTo(markup);
        await Assert.That(Excerpt("")).IsEqualTo("");
        var english = Excerpt(string.Concat(Enumerable.Repeat("introduction ", 10)) + "target " + string.Concat(Enumerable.Repeat("documentation ", 30)));
        await Assert.That(english.StartsWith("…introduction ", StringComparison.Ordinal)).IsTrue();
        await Assert.That(english.EndsWith("documentation…", StringComparison.Ordinal)).IsTrue();
        await Assert.That(english.Length <= 180).IsTrue();
        foreach (var unit in new[] { "説明", "😀", "e\u0301", "👨‍👩‍👧‍👦" })
        {
            var body = string.Concat(Enumerable.Repeat(unit, 70)) + "target" + string.Concat(Enumerable.Repeat(unit, 200));
            var result = Excerpt(body);
            await Assert.That(result.Contains("target", StringComparison.Ordinal)).IsTrue();
            await Assert.That(result.Length <= 180).IsTrue();
            var content = result.Trim('…');
            var start = body.IndexOf(content, StringComparison.Ordinal);
            var boundaries = System.Globalization.StringInfo.ParseCombiningCharacters(body);
            await Assert.That(boundaries.Contains(start)).IsTrue();
            await Assert.That(boundaries.Contains(start + content.Length)).IsTrue();
        }
    }
    [Test]
    public async Task TechnicalQueriesRankExpectedSectionsWithoutMixingContexts()
    {
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Search", "technical-search.json")));
        var documents = fixture.RootElement.GetProperty("documents").Deserialize<SearchDocument[]>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        foreach (var example in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            var expected = example.GetProperty("expected").EnumerateArray().Select(value => value.GetString());
            var hits = LocalSearch.Query(documents, example.GetProperty("query").GetString()!, example.GetProperty("collection").GetString(),
                example.GetProperty("version").GetString(), example.GetProperty("locale").GetString(),
                example.TryGetProperty("limit", out var limit) ? limit.GetInt32() : 30);
            await Assert.That(string.Join('\n', hits.Select(hit => hit.Url))).IsEqualTo(string.Join('\n', expected));
        }
    }
}
