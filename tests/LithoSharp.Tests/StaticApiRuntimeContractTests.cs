using LithoSharp.Diagnostics;
using LithoSharp.Quality;
using LithoSharp.Routing;
using System.Text.Json;

namespace LithoSharp.Tests;

public sealed class StaticApiRuntimeContractTests
{
    [Test]
    public async Task ArgumentEvaluationOrderPreservesValidAndInvalidRuntimeCalls()
    {
        var valid = new[] {
            Fixtures.StaticApiArgumentEvaluationCases.PositionalValid(),
            Fixtures.StaticApiArgumentEvaluationCases.NamedValid(),
            Fixtures.StaticApiArgumentEvaluationCases.NamedReversedValid(),
            Fixtures.StaticApiArgumentEvaluationCases.WithinArgumentValid(),
        };
        foreach (var route in valid)
        {
            await Assert.That(route.PublicPath).IsEqualTo("/ok.html");
            await Assert.That(route.RelativeOutputPath).IsEqualTo("ok.html");
        }
        await Assert.That(Fixtures.StaticApiArgumentEvaluationCases.OptionLaterAssignmentValid().FailureThreshold).IsEqualTo(SiteDiagnosticSeverity.Warning);
        await Assert.That(Fixtures.StaticApiArgumentEvaluationCases.PositionalInvalidFirst).Throws<ArgumentException>();
        await Assert.That(Fixtures.StaticApiArgumentEvaluationCases.NamedReversedInvalidFirst).Throws<UriFormatException>();
        await Assert.That(Fixtures.StaticApiArgumentEvaluationCases.PositionalSwappedInvalidBoth).Throws<ArgumentException>();
        await Assert.That(Fixtures.StaticApiArgumentEvaluationCases.NamedSwappedInvalidBoth).Throws<ArgumentException>();
        await Assert.That(Fixtures.StaticApiArgumentEvaluationCases.OutputNamedReversedInvalidFirst).Throws<ArgumentException>();
        if (Environment.GetEnvironmentVariable("RA03_EVIDENCE_DIR") is { Length: > 0 } directory)
            File.WriteAllText(Path.Combine(directory, "argument-evaluation-runtime.json"), JsonSerializer.Serialize(new
            { status = "PASS", validCases = 5, rejectedCases = 5, relativeOutput = "ok.html", publicPath = "/ok.html", failureThreshold = "Warning",
                sharedSource = "tests/LithoSharp.Tests/Fixtures/StaticApiArgumentEvaluationCases.cs" }, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Test]
    public async Task RuntimeGuardsStillRejectTheAnalyzerCorpusWithoutCompilerDiagnostics()
    {
        var results = new List<object>();
        void Reject(string sink, string? input, Action invoke)
        {
            Exception? failure = null;
            try { invoke(); } catch (Exception e) when (e is ArgumentException or UriFormatException) { failure = e; }
            if (failure is null) throw new InvalidOperationException("Runtime accepted bad " + sink + ": " + input);
            results.Add(new { sink, input, exception = failure.GetType().Name, parameter = (failure as ArgumentException)?.ParamName });
        }
        foreach (var input in new[] { null, "relative", "/tmp/private", " file:///tmp", "file:///tmp", "https://a/a b", "https://a/\\b", "https://a/\0" })
            Reject("SiteUrl.FromAbsolute", input, () => SiteUrl.FromAbsolute(input!));
        foreach (var input in new[] { null, "", "../escape", "%2e%2e", "CON", "%zz", "%ff", "x/", "C:/x", "https://x" })
            Reject("SiteRoute.ForFile", input, () => SiteRoute.ForFile(input!));
        foreach (var input in new[] { null, "../escape", "%2e%2e", "CON" })
            Reject("SiteRoute.ForDirectoryIndex", input, () => SiteRoute.ForDirectoryIndex(input!));
        foreach (var input in new[] { null, "", "../escape", "CON", "/root", "C:/root" })
            Reject("SiteAssetOutput", input, () => new SiteAssetOutput("asset", input!));
        foreach (var value in new[] { -1, 3, 9, int.MaxValue })
            Reject("SiteQualityOptions", value.ToString(), () => new SiteQualityOptions((SiteDiagnosticSeverity)value));
        foreach (var value in Enum.GetValues<SiteDiagnosticSeverity>())
            await Assert.That(new SiteQualityOptions(value).FailureThreshold).IsEqualTo(value);
        await Assert.That(SiteUrl.FromAbsolute("https://example.test/café?q=1").Value).IsEqualTo("https://example.test/café?q=1");
        await Assert.That(SiteRoute.ForDirectoryIndex("").RelativeOutputPath).IsEqualTo("index.html");
        await Assert.That(new SiteAssetOutput("a", "%2e%2e.html").RelativeOutputPath).IsEqualTo("%2e%2e.html");
        // Output paths are filesystem names, not URI segments: percent text stays literal.
        if (Environment.GetEnvironmentVariable("RA03_RUNTIME_PROOF") is { Length: > 0 } path)
            File.WriteAllText(path, JsonSerializer.Serialize(new { status = "PASS", results, validSeverities = Enum.GetValues<SiteDiagnosticSeverity>(),
                analysisSuppressionCannotDisableRuntimeGuard = true }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
