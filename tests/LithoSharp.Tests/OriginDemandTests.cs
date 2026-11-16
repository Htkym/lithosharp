using LithoSharp.Build;
using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Pages;
using LithoSharp.Quality;
using LithoSharp.Routing;

namespace LithoSharp.Tests;

public sealed class OriginDemandTests
{
    [Test]
    public async Task SourceOriginDemandMustPrecedeTheFirstSharedParse()
    {
        const string html = "<a href=\"missing.html\">Missing</a>";
        var identity = new HtmlPageIdentity("index.html", "/index.html", null, null);
        var address = new Uri("https://example.test/index.html");
        await using var declared = new BuildHtmlFacts(default);
        declared.RequireQualityOrigins();
        await declared.GetAsync(html, default);
        await declared.BeginQualityAsync();
        var bound = await declared.GetQualityReferencesAsync(html, identity, address, default);
        await Assert.That(bound.Snapshot.References.Single().Source is not null).IsTrue();
        await Assert.That(declared.Counters.ParseCount).IsEqualTo(1);

        await using var undeclared = new BuildHtmlFacts(default);
        await undeclared.GetAsync(html, default);
        await Assert.That(() => undeclared.RequireQualityOrigins()).Throws<InvalidOperationException>();
        await Assert.That(undeclared.RequiresQualityRaw).IsFalse();
        await Assert.That(undeclared.Counters.ParseCount).IsEqualTo(1);
    }

    [Test]
    public async Task LaterExtensionSourcesDeclareDemandOnTheOriginalPreparationOwner()
    {
        using var workspace = new TemporaryWorkspace();
        var content = Path.Combine(workspace.Root, "content");
        Directory.CreateDirectory(content);
        const string raw = "\uFEFF---\r\ntitle: Guide\r\n---\r\n[bad](#gone)\r\n";
        await File.WriteAllBytesAsync(Path.Combine(content, "guide.md"), System.Text.Encoding.UTF8.GetBytes(raw));
        var loaded = await new MarkdownContentCollectionLoader<FrontMatter>(new("guides"), content,
            static _ => SiteRoute.ForDirectoryIndex("guide"), static entry => new PageMetadata(entry.FrontMatter.Title)).LoadAsync();
        await Assert.That(loaded.IsSuccess).IsTrue();
        var observer = new PreparationObserver();
        var pages = new SiteContentCollection<FrontMatter, string>(loaded.Collection!,
            static (entry, context) => context.RenderDocument(context.RenderMarkdown(entry.Body)));
        var options = new SiteGenerationOptions
        {
            BuildTimestamp = DateTimeOffset.UnixEpoch,
            Quality = new(checkOrphans: false),
            Extensions = [observer, new SourceContributor(pages)],
        };
        SiteQualityValidationException? failure = null;
        try
        {
            await new SiteGenerator().GenerateWithOptionsAsync(new SiteSettings { BaseUrl = "https://example.test/", Title = "Origins" },
                [], Path.Combine(workspace.Root, "output"), false,
                new() { Template = new BlogSiteTemplate(), FaviconSourceDirectory = Path.Combine(workspace.Root, "no-favicon") }, options, default);
        }
        catch (SiteQualityValidationException exception)
        {
            failure = exception;
        }
        await Assert.That(failure is not null).IsTrue();
        await Assert.That(observer.Owner is not null).IsTrue();
        await Assert.That(observer.InitialRawDemand).IsFalse();
        await Assert.That(observer.Owner!.RequiresQualityRaw).IsTrue();
        var diagnostic = failure!.Diagnostics.Single(item => item.Id == "LSQ002");
        await Assert.That(diagnostic.Location!.FilePath).IsEqualTo("guide.md");
        await Assert.That(diagnostic.Location.Line).IsEqualTo(4);
        await Assert.That(diagnostic.Location.Column).IsEqualTo(7);
        await Assert.That(diagnostic.MappingPrecision).IsEqualTo("Exact");
    }

    public sealed class FrontMatter { public string Title { get; init; } = string.Empty; }

    private sealed class PreparationObserver : ISiteBuildExtension
    {
        internal BuildHtmlFacts? Owner { get; private set; }
        internal bool InitialRawDemand { get; private set; }
        public Task<SiteBuildContribution> PrepareAsync(SiteBuildContext context, CancellationToken cancellationToken = default)
        {
            Owner = context.HtmlFacts;
            InitialRawDemand = Owner?.RequiresQualityRaw ?? false;
            return Task.FromResult(new SiteBuildContribution());
        }
    }

    private sealed class SourceContributor(SiteContentCollection collection) : ISiteBuildExtension
    {
        public Task<SiteBuildContribution> PrepareAsync(SiteBuildContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(new SiteBuildContribution { ContentCollections = [collection] });
    }
}
