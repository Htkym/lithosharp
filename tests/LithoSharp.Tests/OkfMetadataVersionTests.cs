using LithoSharp.OpenKnowledge;
using Syntamark;

namespace LithoSharp.Tests;

public sealed class OkfMetadataVersionTests
{
    private static readonly DateTimeOffset AsOf = new(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task CurrentVerificationUsesExactInstantsAndPreservesHistory()
    {
        const string yaml = """
            type: Reference
            generated: {by: producer/v2, at: "2026-11-13T09:00:00.123456782+09:00"}
            verified:
              - {by: human:old-review, at: "2026-11-13T00:00:00.123456781Z", x-acme-proof: old}
              - {by: team:current-review, at: "2026-11-13T00:00:00.123456782Z"}
            """;
        var current = Read(yaml);
        await Assert.That(current.TrustTier).IsEqualTo(OkfTrustTier.MachineConfirmed);
        await Assert.That(current.VerificationEvents[0].IsCurrent).IsFalse();
        await Assert.That(current.VerificationEvents[1].IsCurrent).IsTrue();
        await Assert.That(current.VerificationEvents[0].IsValid).IsTrue();
        await Assert.That(current.VerificationEvents[0].At).IsNull();
        await Assert.That(OkfMetadata.String(OkfMetadata.Field(current.VerificationEvents[0].Raw, "x-acme-proof"))).IsEqualTo("old");
        var legacy = Read(yaml, version03: false);
        await Assert.That(legacy.TrustTier).IsEqualTo(OkfTrustTier.HumanReviewed);
    }

    [Test]
    public async Task DateOnlyIsUtcAndMalformedDateTimeIsNeverFresh()
    {
        var current = Read("""
            type: Reference
            generated: {by: producer/v2, at: 2026-11-13}
            verified: {by: "human:review", at: "2026-11-13T09:00:00+09:00"}
            stale_after: 2026-11-13
            """);
        await Assert.That(current.GeneratedAt).IsEqualTo(AsOf);
        await Assert.That(current.Freshness).IsEqualTo(OkfFreshness.Stale);
        await Assert.That(current.TrustTier).IsEqualTo(OkfTrustTier.HumanReviewed);
        foreach (var malformed in new[] { "2026-11-13T00:00:00", "2026-11-13T00:00Z", "2026-11-13T00:00:00+0900" })
            await Assert.That(Read($"type: Reference\nstale_after: \"{malformed}\"").Freshness).IsEqualTo(OkfFreshness.Invalid);
    }

    [Test]
    public async Task OptionalSingletonsAndExtensionsStayNoncoercing()
    {
        var current = Read("""
            type: Attested Computation
            runtime: test
            tags: "one,two"
            sources: {resource: scope descriptor, x-acme-owner: team:demo}
            parameters: {name: since, type: date, x-acme-note: added}
            stale_after: []
            verified: null
            status: deprecated
            superseded_by: /new.md
            """);
        await Assert.That(current.Tags.Single()).IsEqualTo("one,two");
        await Assert.That(current.Sources.Single().Resource).IsEqualTo("scope descriptor");
        await Assert.That(current.Freshness).IsEqualTo(OkfFreshness.NoDeadline);
        await Assert.That(current.TrustTier).IsEqualTo(OkfTrustTier.Unverified);
        await Assert.That(current.SupersededBy).IsEqualTo("/new.md");
        await Assert.That(current.ParameterEntries.Single().Name).IsEqualTo("since");
        await Assert.That(current.ParameterEntries.Single().Required).IsFalse();
        await Assert.That(OkfMetadata.String(OkfMetadata.Field(current.ParameterEntries.Single().Raw, "x-acme-note"))).IsEqualTo("added");
        var absent = Read("type: Reference\nstatus: null\nsources: []\nverified: ''\n");
        await Assert.That(absent.Status).IsEqualTo("stable");
        await Assert.That(absent.Sources.Count).IsEqualTo(0);
        var unknown = Read("type: Reference\nstatus: retired\nsuperseded_by: /new.md\n");
        await Assert.That(unknown.Status).IsEqualTo("retired");
        await Assert.That(unknown.SupersededBy).IsNull();
    }

    [Test]
    public async Task GenerationWithoutATimestampDoesNotElevateTrust()
    {
        const string yaml = "type: Reference\ngenerated: {by: producer/v2}\nverified: {by: 'human:review', at: 2026-11-13}\n";
        var withGeneration = Read(yaml);
        await Assert.That(withGeneration.TrustTier).IsEqualTo(OkfTrustTier.Unverified);
        await Assert.That(withGeneration.VerificationEvents.Single().IsCurrent).IsFalse();
        var verified = Read("type: Reference\nverified: {by: 'human:review', at: 2026-11-13}\n");
        await Assert.That(verified.TrustTier).IsEqualTo(OkfTrustTier.HumanReviewed);
    }

    private static OkfMetadata Read(string yaml, bool version03 = true)
    {
        var parsed = MarkdownParser.Parse($"---\n{yaml.TrimEnd()}\n---\n", "test", "concept.md").FrontMatter!.GetYamlSyntax();
        return new OkfMetadata(parsed!.Root!, AsOf, static (_, _, _) => { }, version03);
    }
}
