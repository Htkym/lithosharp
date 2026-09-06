using LithoSharp.Content;
using LithoSharp.Diagnostics;

namespace LithoSharp.Tests;

public sealed class GeneratedBindingContextTests
{
    [Test]
    public async Task BindingMatchesReflectionDefaultsAssignmentOrderAndDiagnostics()
    {
        IReadOnlyDictionary<string, object?>[] cases =
        [
            new Dictionary<string, object?> { ["set_fallback"] = "ready", ["count"] = "42" },
            new Dictionary<string, object?> { ["count"] = "invalid", ["fallback"] = null, ["unknown"] = true },
            new Dictionary<string, object?>()
        ];
        foreach (var values in cases)
        {
            var location = new SiteSourceLocation("test.yaml", 2, 1);
            var expected = new ReflectionContentFrontMatterBinder<FrontMatter>().Bind(values, location);
            var actual = Bind(values, location);
            await Assert.That(actual.IsSuccess).IsEqualTo(expected.IsSuccess);
            await Assert.That(string.Join("\n", actual.Diagnostics.Select(d => $"{d.Id}|{d.Message}|{d.Location?.FilePath}:{d.Location?.Line}:{d.Location?.Column}")))
                .IsEqualTo(string.Join("\n", expected.Diagnostics.Select(d => $"{d.Id}|{d.Message}|{d.Location?.FilePath}:{d.Location?.Line}:{d.Location?.Column}")));
            if (actual.IsSuccess)
            {
                await Assert.That(actual.Value!.Fallback).IsEqualTo(expected.Value!.Fallback);
                await Assert.That(actual.Value.Count).IsEqualTo(expected.Value.Count);
            }
        }
    }

    private static ContentParseResult<FrontMatter> Bind(IReadOnlyDictionary<string, object?> values, SiteSourceLocation location)
    {
        var context = new GeneratedContentBindingContext(values, location);
        var result = new FrontMatter();
        foreach (var key in context.Values.Keys)
        {
            switch (key)
            {
                case "fallback":
                    if (context.TryRead(key, result.Fallback, false, false,
                        GeneratedContentBindingContext.TryParseScalar<string>, out var fallback)) result.Fallback = fallback;
                    break;
                case "set_fallback":
                    if (context.TryRead(key, result.SetFallback, false, false,
                        GeneratedContentBindingContext.TryParseScalar<string>, out var setter)) result.SetFallback = setter;
                    break;
                case "count":
                    if (context.TryRead(key, result.Count, false, false,
                        GeneratedContentBindingContext.TryParseScalar<int>, out var count)) result.Count = count;
                    break;
            }
        }
        context.ValidateRequired("fallback", result.Fallback, false, false);
        context.ValidateRequired("set_fallback", result.SetFallback, false, false);
        context.ValidateRequired("count", result.Count, false, false);
        context.ValidateUnknownFields();
        return context.Complete(result);
    }

    public sealed class FrontMatter
    {
        public string Fallback { get; set; } = null!;
        public string SetFallback { get => Fallback ?? string.Empty; set => Fallback = value; }
        public int Count { get; set; } = 7;
    }
}
