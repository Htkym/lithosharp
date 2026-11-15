using LithoSharp.AgentReady;
using LithoSharp.Configuration;
using LithoSharp.Content;
using LithoSharp.Content.Compilation;
using LithoSharp.Diagnostics;
using Syntamark.Compilation;

namespace LithoSharp.Tests;

public sealed class AgentReadyBodyTests
{
    [Test]
    [Arguments(MarkdownOptions.LegacyProfile)]
    [Arguments(MarkdownOptions.CommonMarkProfile)]
    [Arguments(MarkdownOptions.DocumentationProfile)]
    public async Task DestinationEditsPreserveSharedReferencesTitlesUnicodeAndBodyProfile(string profile)
    {
        const string raw = "---\r\n\r\n# 😀 Title\r\n\r\n[guide][shared] and [again][shared]\r\n\r\n[shared]: old.md \"Keep title\"\r\n";
        const string destination = "https://example.test/日本/new)part?a=1&copy;=2|3#section";
        var compiler = Compiler(profile);
        var analysis = compiler.Analyze(raw, new("source.md", 0));
        var result = PublicMarkdownBody.Convert(analysis, compiler.Options.Markdown, null, _ => destination, default);
        await Assert.That(result.IsComplete).IsTrue();
        await Assert.That(result.Body!).StartsWith("---\r\n\r\n# 😀 Title");
        await Assert.That(result.Body!).Contains("\"Keep title\"");
        var reparsed = compiler.Analyze(result.Body!, new("public.md", 0)).BodyAnalysis!.Analysis;
        await Assert.That(reparsed.Links!.All(link => link.Target == destination)).IsTrue();
        await Assert.That(reparsed.Links!.Where(link => link.Kind == MdLinkKind.ReferenceUse).Count()).IsEqualTo(2);
        await Assert.That(reparsed.BodySpan!.Value.Start).IsEqualTo(0);
    }

    [Test]
    [Arguments(MarkdownOptions.LegacyProfile)]
    [Arguments(MarkdownOptions.CommonMarkProfile)]
    [Arguments(MarkdownOptions.DocumentationProfile)]
    public async Task CommentsAreRemovedOnlyThroughSyntaxWhileCodeAndWhitespaceRemain(string profile)
    {
        const string raw = "Before<!-- PRIVATE *emphasis* -->after\r\n\r\n```html\r\n<!-- EXAMPLE -->\r\n```\r\n\n\\<!-- escaped --> and &lt;!-- entity --> and `<!-- inline code -->`\n";
        var compiler = Compiler(profile);
        var result = PublicMarkdownBody.Convert(compiler.Analyze(raw, new("page.md", 0)), compiler.Options.Markdown, null, link => link.Target, default);
        await Assert.That(result.IsComplete).IsTrue();
        await Assert.That(result.Body!).DoesNotContain("PRIVATE");
        await Assert.That(result.Body!).Contains("<!-- EXAMPLE -->");
        await Assert.That(result.Body!).Contains("\\<!-- escaped -->");
        await Assert.That(result.Body!).Contains("&lt;!-- entity -->");
        await Assert.That(result.Body!).Contains("`<!-- inline code -->`");
        await Assert.That(result.Body!.Count(character => character == '\r')).IsEqualTo(raw.Count(character => character == '\r'));
        await Assert.That(result.Body!).DoesNotContain("Beforeafter");
    }

    [Test]
    [Arguments("<>")]
    [Arguments("<Widget secret='x' />")]
    [Arguments(":::unknown\nHidden meaning\n:::")]
    public async Task UnknownPublicRepresentationsDoNotBecomeComplete(string raw)
    {
        var compiler = Compiler();
        var result = PublicMarkdownBody.Convert(compiler.Analyze(raw, new("page.md", 0)), compiler.Options.Markdown, null, link => link.Target, default);
        await Assert.That(result.IsComplete).IsFalse();
        await Assert.That(result.Body).IsNull();
    }

    [Test]
    [Arguments(MarkdownOptions.LegacyProfile)]
    [Arguments(MarkdownOptions.DocumentationProfile)]
    public async Task LiteralCommentsCannotCrossStructuralPrefixes(string profile)
    {
        const string raw = "> <!-- PRIVATE\n> prefix -->\n";
        var compiler = Compiler(profile);
        var result = PublicMarkdownBody.Convert(compiler.Analyze(raw, new("page.md", 0)), compiler.Options.Markdown, null, link => link.Target, default);
        await Assert.That(result.IsComplete).IsFalse();
        await Assert.That(result.Body).IsNull();
    }

    [Test]
    [Arguments("<!-->")]
    [Arguments("<!--->")]
    public async Task RecognizedShortCommonMarkCommentsAreRemoved(string comment)
    {
        var compiler = Compiler(MarkdownOptions.CommonMarkProfile);
        var result = PublicMarkdownBody.Convert(compiler.Analyze("Before" + comment + "after", new("page.md", 0)), compiler.Options.Markdown, null, link => link.Target, default);
        await Assert.That(result.IsComplete).IsTrue();
        await Assert.That(result.Body!).DoesNotContain("<!--");
        await Assert.That(result.Body!).Contains("Before");
        await Assert.That(result.Body!).Contains("after");
    }

    [Test]
    public async Task AdmonitionsRetainKindTitleAndAllBodyText()
    {
        const string raw = ":::warning title=\"Do not omit\"\nImportant [guide](guide.html).\n:::";
        var compiler = Compiler();
        var result = PublicMarkdownBody.Convert(compiler.Analyze(raw, new("page.md", 0)), compiler.Options.Markdown, null,
            link => "https://example.test/" + link.Target, default);
        await Assert.That(result.IsComplete).IsTrue();
        await Assert.That(result.Body!).Contains("**warning: Do not omit**");
        await Assert.That(result.Body!).Contains("Important [guide](https://example.test/guide.html).");
    }

    [Test]
    public async Task LegacyAdmonitionKeepsItsParsedTitle()
    {
        const string raw = ":::warning Critical safety notice\nBody\n:::";
        var compiler = Compiler(MarkdownOptions.LegacyProfile);
        var result = PublicMarkdownBody.Convert(compiler.Analyze(raw, new("page.md", 0)), compiler.Options.Markdown, null, link => link.Target, default);
        await Assert.That(result.IsComplete).IsTrue();
        await Assert.That(result.Body!).Contains("**warning: Critical safety notice**");
        await Assert.That(result.Body!).Contains("Body");
    }

    [Test]
    [Arguments(MarkdownOptions.LegacyProfile)]
    [Arguments(MarkdownOptions.DocumentationProfile)]
    public async Task KnownAlertQuotesKeepTheirOriginalWarningAndBody(string profile)
    {
        const string raw = "> [!WARNING]\n> Critical warning.\n\n<!-- private -->";
        var compiler = Compiler(profile);
        var result = PublicMarkdownBody.Convert(compiler.Analyze(raw, new("page.md", 0)), compiler.Options.Markdown, null, link => link.Target, default);
        await Assert.That(result.IsComplete).IsTrue();
        await Assert.That(result.Body!).StartsWith("> [!WARNING]\n> Critical warning.");
        await Assert.That(result.Body!).DoesNotContain("private");
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ComponentDefinitionsCannotChangeExistingFootnoteBinding(bool initiallyResolved)
    {
        var registry = new MarkdownComponentRegistry([MarkdownComponentRegistration.Create("public", new FootnoteComponent(), new CountingBinder(), "footnote:v1")]);
        var compiler = Compiler(components: registry);
        var raw = ":::component name=\"public\" text=\"Value\"\n:::\n\nText[^n]\n"
            + (initiallyResolved ? "\n[^n]: Original\n" : "");
        var result = compiler.Analyze(raw, new("page.md", 0), default, null, ComponentRenderingContext.Create(new SiteSettings()));
        var converted = PublicMarkdownBody.Convert(result, compiler.Options.Markdown, null, link => link.Target, default);
        await Assert.That(converted.Reason).IsEqualTo(initiallyResolved
            ? "The transformed body's authoritative analysis is unavailable."
            : "The authoritative body analysis is unavailable or incomplete.");
        await Assert.That(converted.Status).IsEqualTo(PublicMarkdownStatus.Unavailable);
        await Assert.That(converted.Body).IsNull();
    }

    [Test]
    public async Task CompleteFootnotesKeepDefinitionsWhileOtherBodyEditsProceed()
    {
        const string raw = "Text[^n]\n\n[^n]: Original [guide](guide.html)\n\n<!-- private -->";
        var compiler = Compiler();
        var result = PublicMarkdownBody.Convert(compiler.Analyze(raw, new("page.md", 0)), compiler.Options.Markdown, null,
            link => "https://example.test/guide.html", default);
        await Assert.That(result.IsComplete).IsTrue();
        await Assert.That(result.Body!).Contains("Text[^n]");
        await Assert.That(result.Body!).Contains("[^n]: Original [guide](https://example.test/guide.html)");
        await Assert.That(result.Body!).DoesNotContain("private");
    }

    [Test]
    [Arguments("Ordinary text after the component.")]
    [Arguments("[real](guide.html)")]
    [Arguments("Text[^n]\n\n[^n]: Original")]
    public async Task ProviderCannotSwallowFollowingBodyWithAnUnclosedFence(string following)
    {
        var registry = new MarkdownComponentRegistry([MarkdownComponentRegistration.Create("public", new BoundaryComponent("```text\n"), new CountingBinder(), "boundary:v1")]);
        var compiler = Compiler(components: registry);
        var raw = ":::component name=\"public\" text=\"Value\"\n:::\n\n" + following;
        var result = compiler.Analyze(raw, new("page.md", 0), default, null, ComponentRenderingContext.Create(new SiteSettings()));
        var converted = PublicMarkdownBody.Convert(result, compiler.Options.Markdown, null, link => link.Target, default);
        await Assert.That(converted.Status).IsEqualTo(PublicMarkdownStatus.Unsupported);
        await Assert.That(converted.Body).IsNull();
    }

    [Test]
    public async Task ProviderCannotTurnPrecedingParagraphIntoASetextHeading()
    {
        var registry = new MarkdownComponentRegistry([MarkdownComponentRegistration.Create("public", new BoundaryComponent("---\n"), new CountingBinder(), "boundary:v1")]);
        var compiler = Compiler(components: registry);
        const string raw = "Keep this paragraph\n:::component name=\"public\" text=\"Value\"\n:::";
        var result = compiler.Analyze(raw, new("page.md", 0), default, null, ComponentRenderingContext.Create(new SiteSettings()));
        var converted = PublicMarkdownBody.Convert(result, compiler.Options.Markdown, null, link => link.Target, default);
        await Assert.That(converted.Status).IsEqualTo(PublicMarkdownStatus.Unsupported);
        await Assert.That(converted.Body).IsNull();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CompleteProviderBlockPreservesFollowingBody(bool removeComment)
    {
        var registry = new MarkdownComponentRegistry([MarkdownComponentRegistration.Create("public", new BoundaryComponent("```text\nExample\n```\n"), new CountingBinder(), "boundary:v1")]);
        var compiler = Compiler(components: registry);
        var raw = (removeComment ? "<!-- private -->\n\n" : "") + ":::component name=\"public\" text=\"Value\"\n:::\n\n[real](guide.html)";
        var result = compiler.Analyze(raw, new("page.md", 0), default, null, ComponentRenderingContext.Create(new SiteSettings()));
        var converted = PublicMarkdownBody.Convert(result, compiler.Options.Markdown, null, link => "https://example.test/guide.html", default);
        await Assert.That(converted.IsComplete).IsTrue();
        await Assert.That(converted.Body!).Contains("[real](https://example.test/guide.html)");
    }

    private sealed class BoundaryComponent(string markdown) : ISiteComponent<Props>, IPublicMarkdownComponent<Props>
    {
        public IHtmlContent Render(Props props, ComponentRenderingContext context) => new HtmlText(props.Text);
        public string RenderPublicMarkdown(Props props, ComponentRenderingContext context) => markdown;
    }
    private sealed class FootnoteComponent : ISiteComponent<Props>, IPublicMarkdownComponent<Props>
    {
        public IHtmlContent Render(Props props, ComponentRenderingContext context) => new HtmlText(props.Text);
        public string RenderPublicMarkdown(Props props, ComponentRenderingContext context) => "[^n]: Replacement\n";
    }

    [Test]
    public async Task IncludedLinkUsesExactOriginalSourceAfterCommentTransformation()
    {
        const string raw = "<!-- internal -->\n[guide](guide.html \"title\")\n";
        var included = new MdSourceSnapshot("includes/chapter.md", "original-1", raw);
        var expanded = new MdSourceSnapshot("page.md", "include-output", raw);
        var map = new MdSourceMap(expanded, [new(new(0, raw.Length), new(included, new(0, raw.Length)), MdSegmentKind.Linear)]);
        var source = new DocumentSource("page.md", 0) { SourceMap = map, ExactBody = raw };
        var compiler = Compiler();
        var seen = new List<string>();
        var result = PublicMarkdownBody.Convert(compiler.Analyze(raw, source), compiler.Options.Markdown, source, link =>
        {
            seen.Add(link.SourceId);
            return "https://example.test/includes/guide.html";
        }, default);
        await Assert.That(result.IsComplete).IsTrue();
        await Assert.That(seen).IsEquivalentTo(new[] { "includes/chapter.md" });
        await Assert.That(result.Body!).Contains("https://example.test/includes/guide.html");
    }

    [Test]
    [Arguments(MarkdownOptions.LegacyProfile)]
    [Arguments(MarkdownOptions.CommonMarkProfile)]
    [Arguments(MarkdownOptions.DocumentationProfile)]
    public async Task UnresolvedReferenceLiteralsRemainWhileRealLinksAreRewritten(string profile)
    {
        const string raw = "[missing][unknown] ![missing image][absent]\n\n[real](guide.html)";
        var compiler = Compiler(profile);
        var calls = 0;
        var result = PublicMarkdownBody.Convert(compiler.Analyze(raw, new("page.md", 0)), compiler.Options.Markdown, null, link =>
        {
            calls++;
            return "https://example.test/guide.html";
        }, default);
        await Assert.That(result.IsComplete).IsTrue();
        await Assert.That(result.Body!).StartsWith("[missing][unknown] ![missing image][absent]");
        await Assert.That(result.Body!).Contains("[real](https://example.test/guide.html)");
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task ComponentDefinitionsCannotChangeExistingReferenceMeaning()
    {
        var registry = new MarkdownComponentRegistry([MarkdownComponentRegistration.Create("public", new ShadowingComponent(), new CountingBinder(), "shadow:v1")]);
        var compiler = Compiler(components: registry);
        const string raw = "[missing][absent]\n\n:::component name=\"public\" text=\"Value\"\n:::";
        var result = compiler.Analyze(raw, new("page.md", 0), default, null, ComponentRenderingContext.Create(new SiteSettings()));
        var converted = PublicMarkdownBody.Convert(result, compiler.Options.Markdown, null, link => link.Target, default);
        await Assert.That(converted.Status).IsEqualTo(PublicMarkdownStatus.Unsupported);
        await Assert.That(converted.Body).IsNull();
    }

    private sealed class ShadowingComponent : ISiteComponent<Props>, IPublicMarkdownComponent<Props>
    {
        public IHtmlContent Render(Props props, ComponentRenderingContext context) => new HtmlText(props.Text);
        public string RenderPublicMarkdown(Props props, ComponentRenderingContext context) => "[absent]: altered.html";
    }

    [Test]
    public async Task MissingOrStaleEvidenceFailsClosedWithoutFallbackParse()
    {
        const string raw = "[guide](guide.html)";
        var compiler = Compiler();
        var result = compiler.Analyze(raw, new("page.md", 0));
        var wrong = new MarkdownBodyAnalysisSnapshot(MarkdownAnalysisAdapter.AnalyzeBody(raw, "page.md",
            MarkdownAnalysisAdapter.BodyOptionsFor(compiler.Options.Markdown), MarkdownAnalysisAdapter.BodyPartsFor(compiler.Options.Markdown, true), "stale", default));
        foreach (var candidate in new[] { result with { BodyAnalysis = null }, result with { BodyAnalysis = wrong } })
        {
            var converted = PublicMarkdownBody.Convert(candidate, compiler.Options.Markdown, null, link => link.Target, default);
            await Assert.That(converted.Status).IsEqualTo(PublicMarkdownStatus.Unavailable);
        }
        await Assert.That(compiler.ParseCount).IsEqualTo(1);
    }

    [Test]
    public async Task ComponentHtmlAndMarkdownShareBoundPropsAndOffDoesNotRunProvider()
    {
        var binder = new CountingBinder();
        var component = new PublicComponent();
        var registry = new MarkdownComponentRegistry([MarkdownComponentRegistration.Create("public", component, binder, "public-component:v1")]);
        const string raw = ":::component name=\"public\" text=\"Keep the warning\"\n:::";
        var compiler = Compiler(components: registry);
        var result = compiler.Analyze(raw, new("page.md", 0), default, null, ComponentRenderingContext.Create(new SiteSettings()));
        var converted = PublicMarkdownBody.Convert(result, compiler.Options.Markdown, null, link => link.Target, default);
        await Assert.That(converted.Body).IsEqualTo("**Keep the warning**");
        await Assert.That(result.Html).Contains("Keep the warning");
        await Assert.That(binder.Calls).IsEqualTo(1);
        await Assert.That(component.HtmlCalls).IsEqualTo(1);
        await Assert.That(component.MarkdownCalls).IsEqualTo(1);
        await Assert.That(ReferenceEquals(component.HtmlProps, component.MarkdownProps)).IsTrue();
        _ = new LithoMarkdownCompiler(compiler.Options with { RetainAgentReadyFacts = false })
            .Analyze(raw, new("page.md", 0), default, null, ComponentRenderingContext.Create(new SiteSettings()));
        await Assert.That(component.MarkdownCalls).IsEqualTo(1);
    }

    [Test]
    public async Task ProviderFailurePreservesHtmlAndDoesNotExposeExceptionText()
    {
        var registry = new MarkdownComponentRegistry([MarkdownComponentRegistration.Create("public", new FailingPublicComponent(), new CountingBinder(), "failing:v1")]);
        var compiler = Compiler(components: registry);
        var result = compiler.Analyze(":::component name=\"public\" text=\"Warning\"\n:::", new("page.md", 0), default, null, ComponentRenderingContext.Create(new SiteSettings()));
        var converted = PublicMarkdownBody.Convert(result, compiler.Options.Markdown, null, link => link.Target, default);
        await Assert.That(result.Html).Contains("Warning");
        await Assert.That(result.Semantics!.Diagnostics.Any(item => item.Severity == SiteDiagnosticSeverity.Error)).IsFalse();
        await Assert.That(converted.Status).IsEqualTo(PublicMarkdownStatus.Failed);
        await Assert.That(converted.Reason!).DoesNotContain("PRIVATE");
    }

    private sealed class FailingPublicComponent : ISiteComponent<Props>, IPublicMarkdownComponent<Props>
    {
        public IHtmlContent Render(Props props, ComponentRenderingContext context) => new HtmlText(props.Text);
        public string? RenderPublicMarkdown(Props props, ComponentRenderingContext context) => throw new InvalidOperationException("PRIVATE input");
    }

    private static LithoMarkdownCompiler Compiler(string profile = MarkdownOptions.DocumentationProfile, MarkdownComponentRegistry? components = null) =>
        new(MarkdownCompilerOptions.Default with { RetainAgentReadyFacts = true, Markdown = new() { ProfileId = profile, Components = components } });

    private sealed record Props(string Text);
    private sealed class CountingBinder : IContentFrontMatterBinder<Props>
    {
        internal int Calls;
        public ContentParseResult<Props> Bind(IReadOnlyDictionary<string, object?> values, SiteSourceLocation? sourceLocation = null)
        {
            Calls++;
            return ContentParseResult<Props>.Success(new((string)values["text"]!));
        }
    }
    private sealed class PublicComponent : ISiteComponent<Props>, IPublicMarkdownComponent<Props>
    {
        internal int HtmlCalls, MarkdownCalls;
        internal Props? HtmlProps, MarkdownProps;
        public IHtmlContent Render(Props props, ComponentRenderingContext context)
        {
            HtmlCalls++; HtmlProps = props;
            return new HtmlText(props.Text);
        }
        public string RenderPublicMarkdown(Props props, ComponentRenderingContext context)
        {
            MarkdownCalls++; MarkdownProps = props;
            return "**" + props.Text + "**";
        }
    }
}