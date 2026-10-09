using System.Text.Json;
using LithoSharp.HtmlParsing;
using LithoSharp.HtmlPortability;

namespace LithoSharp.Tests;

public sealed class HtmlPortableBridgeTests
{
    [Test]
    public async Task LiteralUrlFactsPreserveRawMappingNamespacesAndFailClosedCoverage()
    {
        var inputs = HtmlBridgeObservations.Inputs(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "HtmlObservation", "html-observation-corpus-v1.json"));
        var observations = new List<object>();
        foreach (var input in inputs)
        {
            var facts = (HtmlLiteralFacts)HtmlBridgeObservations.Parse(typeof(HtmlLiteralFacts).Assembly, input);
            if (input.Id.StartsWith("unsupported-", StringComparison.Ordinal) || input.Id == "partial-node-budget")
            {
                await Assert.That(facts.Status).IsEqualTo(HtmlTokenizationStatus.Partial);
                await Assert.That(facts.Urls.Count).IsEqualTo(0);
                await Assert.That(facts.Diagnostics.Any(d => d.IncompleteCoverage)).IsTrue();
            }
            else if (input.Id == "failed-input-budget")
            {
                await Assert.That(facts.Status).IsEqualTo(HtmlTokenizationStatus.Failed);
                await Assert.That(facts.Urls.Count).IsEqualTo(0);
                await Assert.That(facts.TokenizerDiagnostics.Any(d => d.Code == "input-char-budget")).IsTrue();
            }
            else await Assert.That(facts.Status).IsEqualTo(HtmlTokenizationStatus.Complete);
            if (input.Id == "raw-entity-position")
            {
                await Assert.That(facts.Urls.Count).IsEqualTo(3);
                var first = facts.Urls[0];
                await Assert.That(first.ElementName).IsEqualTo("a");
                await Assert.That(first.RawValue).IsEqualTo(" HTTP://Example.COM/%2f/?x=&amp;amp;&y=e\u0301#Ä ");
                await Assert.That(first.Value.Value).IsEqualTo(" HTTP://Example.COM/%2f/?x=&amp;&y=e\u0301#Ä ");
                await Assert.That(first.Value.Source.Start).IsEqualTo(28);
                await Assert.That(first.Line).IsEqualTo(2);
                await Assert.That(first.Column).IsEqualTo(12);
                await Assert.That(facts.Urls[1].Value.Value).IsEqualTo("&notit=");
                await Assert.That(facts.Urls[2].Value.Value).IsEqualTo("€�");
                var entity = first.Value.Segments.Single(s => input.Html.Substring(s.Source.Start, s.Source.Length) == "&amp;");
                await Assert.That(entity.ValueLength).IsEqualTo(1);
                await Assert.That(entity.Source.Length).IsEqualTo(5);
            }
            if (input.Id == "namespace-duplicate")
            {
                await Assert.That(facts.Urls.Select(u => u.Value.Value).ToArray()).IsEquivalentTo(new[] { "a&b", "plain", "h/é", "math-html" });
                await Assert.That(facts.Urls[0].ElementNamespace).IsEqualTo(HtmlNamespaces.Svg);
                await Assert.That(facts.Urls[0].AttributeNamespace).IsEqualTo(HtmlNamespaces.XLink);
                await Assert.That(facts.Urls[0].AttributePrefix).IsEqualTo("xlink");
                await Assert.That(facts.Urls[1].AttributeNamespace).IsNull();
                await Assert.That(facts.Urls[2].ElementNamespace).IsEqualTo(HtmlNamespaces.Html);
                await Assert.That(facts.Urls[3].ElementNamespace).IsEqualTo(HtmlNamespaces.Html);
                await Assert.That(facts.TokenizerDiagnostics.Any(d => d.Code == "duplicate-attribute")).IsTrue();
            }
            if (input.Id is "inert-off" or "inert-on")
                await Assert.That(string.Join(",", facts.Urls.Select(u => u.Value.Value))).IsEqualTo(input.Scripting ? "active" : "fallback,active");
            if (input.Id == "url-attributes")
                await Assert.That(string.Join(",", facts.Urls.Select(u => u.AttributeName))).IsEqualTo("href,action,formaction,poster,src,data,srcset");
            observations.Add(new { input.Id, facts = JsonSerializer.SerializeToElement(facts) });
        }
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.That(() => HtmlLiteralFacts.Parse("<a href='x'>X</a>", cancellationToken: cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(() => HtmlLiteralFacts.Parse(null!)).Throws<ArgumentNullException>();
        if (Environment.GetEnvironmentVariable("HT07_RUNTIME_PROOF") is { Length: > 0 } proof)
            File.WriteAllText(proof, JsonSerializer.Serialize(observations, new JsonSerializerOptions { WriteIndented = true }));
    }
}
