using LithoSharp.Website.Examples;

public sealed class ExampleTests
{
    [Test]
    public async Task PublishedDocumentsRequireAnExplicitVersion()
    {
        await Assert.That(WebsiteExamples.HasPublishedVersion(false, null)).IsFalse();
        await Assert.That(WebsiteExamples.HasPublishedVersion(false, " ")).IsFalse();
        await Assert.That(WebsiteExamples.HasPublishedVersion(false, "3.0")).IsTrue();
        await Assert.That(WebsiteExamples.HasPublishedVersion(true, null)).IsTrue();
    }
}
