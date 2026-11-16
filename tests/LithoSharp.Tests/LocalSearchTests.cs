using System.Text.Json;
using LithoSharp.Search;

namespace LithoSharp.Tests;

public sealed class LocalSearchTests
{
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
