using LithoSharp.Pages;
using LithoSharp.Publishing;
using LithoSharp.Routing;

namespace LithoSharp.Tests;

public sealed class PageModelTests
{
    [Test]
    public async Task PageId_UsesNormalizedOrdinalEquality()
    {
        var composed = new PageId("post:caf\u00E9");
        var decomposed = new PageId("post:cafe\u0301");
        var differentCase = new PageId("post:Caf\u00E9");

        await Assert.That(composed.Value).IsEqualTo("post:caf\u00E9");
        await Assert.That(composed.Equals(decomposed)).IsTrue();
        await Assert.That(composed.Equals((object)decomposed)).IsTrue();
        await Assert.That(composed.GetHashCode()).IsEqualTo(decomposed.GetHashCode());
        await Assert.That(composed.Equals(differentCase)).IsFalse();
        await Assert.That(composed.ToString()).IsEqualTo(composed.Value);
    }

    [Test]
    [Arguments("")]
    [Arguments(" ")]
    [Arguments(" page")]
    [Arguments("page ")]
    [Arguments("page\nchild")]
    public async Task PageId_RejectsInvalidValues(string value)
    {
        await Assert.That(() => new PageId(value)).Throws<ArgumentException>();
        await Assert.That(() => new PageId(null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task SitePage_RetainsTypedContentAndRoute()
    {
        var id = new PageId("post:guide");
        var route = SiteRoute.ForDirectoryIndex("guide");
        var content = new TestContent("typed");
        var metadata = new PageMetadata(title: "Guide", description: "Description");

        var page = new SitePage<TestContent>(id, route, content, metadata);

        await Assert.That(page.Id).IsSameReferenceAs(id);
        await Assert.That(page.Route).IsSameReferenceAs(route);
        await Assert.That(page.Content).IsSameReferenceAs(content);
        await Assert.That(page.Metadata).IsSameReferenceAs(metadata);
    }

    [Test]
    public async Task SitePage_ContentContractIsNotNull()
    {
        var openType = typeof(SitePage<>);
        var contentParameter = openType
            .GetConstructors()
            .Single()
            .GetParameters()
            .Single(parameter => parameter.Name == "content");
        var contentProperty = openType.GetProperty(nameof(SitePage<object>.Content))!;
        var nullability = new System.Reflection.NullabilityInfoContext();

        await Assert.That(nullability.Create(contentParameter).ReadState)
            .IsEqualTo(System.Reflection.NullabilityState.NotNull);
        await Assert.That(nullability.Create(contentProperty).ReadState)
            .IsEqualTo(System.Reflection.NullabilityState.NotNull);

        var id = new PageId("post:null");
        var route = SiteRoute.ForFile("null.html");
        var metadata = new PageMetadata();
        await Assert.That(() => new SitePage<string>(id, route, null!, metadata))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task RenderedPage_BindsSourceIdentityRouteContentAndMetadata()
    {
        var id = new PageId("post:guide");
        var route = SiteRoute.ForFile("guide.html");
        var metadata = new PageMetadata(title: "Guide");

        var page = new RenderedPage(id, route, "<main>Guide</main>", metadata);

        await Assert.That(page.SourceId).IsSameReferenceAs(id);
        await Assert.That(page.Route).IsSameReferenceAs(route);
        await Assert.That(page.Content).IsEqualTo("<main>Guide</main>");
        await Assert.That(page.Metadata).IsSameReferenceAs(metadata);
    }

    [Test]
    public async Task Publication_DraftIsNeverPublished()
    {
        var metadata = new PageMetadata(draft: true);

        var result = PagePublicationPolicy.ShouldPublish(
            metadata,
            DateTimeOffset.Parse("2026-08-30T00:00:00Z"),
            "Production");

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task Publication_EnvironmentMatchingIsCaseInsensitive()
    {
        var source = new[] { "Staging", "PRODUCTION", "staging" };
        var metadata = new PageMetadata(environments: source);
        source[0] = "Changed";

        await Assert.That(metadata.Environments).IsEquivalentTo(["PRODUCTION", "Staging"]);
        await Assert.That(PagePublicationPolicy.ShouldPublish(
            metadata,
            DateTimeOffset.Parse("2026-08-30T00:00:00Z"),
            "production")).IsTrue();
        await Assert.That(PagePublicationPolicy.ShouldPublish(
            metadata,
            DateTimeOffset.Parse("2026-08-30T00:00:00Z"),
            "Development")).IsFalse();
        await Assert.That(PagePublicationPolicy.ShouldPublish(
            new PageMetadata(),
            DateTimeOffset.Parse("2026-08-30T00:00:00Z"),
            "Development")).IsTrue();
    }

    [Test]
    public async Task Publication_UsesInclusiveStartAndExclusiveEnd()
    {
        var metadata = new PageMetadata(
            publishFrom: DateTimeOffset.Parse("2026-08-30T10:00:00Z"),
            publishUntil: DateTimeOffset.Parse("2026-08-30T11:00:00Z"));

        await Assert.That(PagePublicationPolicy.ShouldPublish(
            metadata,
            DateTimeOffset.Parse("2026-08-30T09:59:59.9999999Z"),
            "Production")).IsFalse();
        await Assert.That(PagePublicationPolicy.ShouldPublish(
            metadata,
            DateTimeOffset.Parse("2026-08-30T10:00:00Z"),
            "Production")).IsTrue();
        await Assert.That(PagePublicationPolicy.ShouldPublish(
            metadata,
            DateTimeOffset.Parse("2026-08-30T10:59:59.9999999Z"),
            "Production")).IsTrue();
        await Assert.That(PagePublicationPolicy.ShouldPublish(
            metadata,
            DateTimeOffset.Parse("2026-08-30T11:00:00Z"),
            "Production")).IsFalse();
    }

    [Test]
    public async Task Publication_NormalizesUtcOffsets()
    {
        var metadata = new PageMetadata(
            publishFrom: DateTimeOffset.Parse("2026-08-30T19:00:00+09:00"),
            publishUntil: DateTimeOffset.Parse("2026-08-30T20:00:00+09:00"));

        await Assert.That(metadata.PublishFrom!.Value.Offset).IsEqualTo(TimeSpan.Zero);
        await Assert.That(metadata.PublishUntil!.Value.Offset).IsEqualTo(TimeSpan.Zero);
        await Assert.That(PagePublicationPolicy.ShouldPublish(
            metadata,
            DateTimeOffset.Parse("2026-08-30T05:30:00-04:00"),
            "Production")).IsFalse();
        await Assert.That(PagePublicationPolicy.ShouldPublish(
            metadata,
            DateTimeOffset.Parse("2026-08-30T06:00:00-04:00"),
            "Production")).IsTrue();
        await Assert.That(PagePublicationPolicy.ShouldPublish(
            metadata,
            DateTimeOffset.Parse("2026-08-30T07:00:00-04:00"),
            "Production")).IsFalse();
    }

    [Test]
    public async Task Metadata_RejectsEmptyOrReversedPublicationIntervals()
    {
        var timestamp = DateTimeOffset.Parse("2026-08-30T10:00:00Z");

        await Assert.That(() => new PageMetadata(
            publishFrom: timestamp,
            publishUntil: timestamp)).Throws<ArgumentException>();
        await Assert.That(() => new PageMetadata(
            publishFrom: timestamp,
            publishUntil: timestamp.AddTicks(-1))).Throws<ArgumentException>();
    }

    [Test]
    public async Task Metadata_RejectsNullOrEmptyEnvironmentConditions()
    {
        await Assert.That(() => new PageMetadata(environments: [null!]))
            .Throws<ArgumentException>();
        await Assert.That(() => new PageMetadata(environments: [""]))
            .Throws<ArgumentException>();
        await Assert.That(() => new PageMetadata(environments: [" "]))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Publication_RejectsNullOrEmptyEnvironmentName()
    {
        var metadata = new PageMetadata();
        var timestamp = DateTimeOffset.Parse("2026-08-30T10:00:00Z");

        await Assert.That(() => PagePublicationPolicy.ShouldPublish(metadata, timestamp, null!))
            .Throws<ArgumentNullException>();
        await Assert.That(() => PagePublicationPolicy.ShouldPublish(metadata, timestamp, ""))
            .Throws<ArgumentException>();
        await Assert.That(() => PagePublicationPolicy.ShouldPublish(metadata, timestamp, " "))
            .Throws<ArgumentException>();
    }

    private sealed record TestContent(string Value);
}
