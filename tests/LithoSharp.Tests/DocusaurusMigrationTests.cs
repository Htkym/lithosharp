using System.Text.Json;
using LithoSharp.Configuration;
using LithoSharp.Documentation;
using LithoSharp.Mdx;

namespace LithoSharp.Tests;

/// <summary>
/// C10: read-only analysis becomes reusable Core migration processing with a
/// converting command form. Dry runs write nothing, real conversions land in
/// a separate output, routes (base path, trailing slash, Unicode, versions,
/// locales) compare clean, and exit codes separate done from unconvertible
/// from failure.
/// </summary>
public sealed class DocusaurusMigrationTests
{
    private static readonly DocusaurusMigrationOptions Options = new("https://example.test/mig/", "en");

    private static async Task<string> WriteSourceAsync(string root)
    {
        var site = Path.Combine(root, "site");
        async Task Write(string relative, string text)
        {
            var path = Path.Combine(site, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, text);
        }

        await Write("docusaurus.config.js", "export default {};\n");
        await Write("sidebars.js", "export default {};\n");
        await Write("versions.json", "[\"1.0\"]\n");
        await Write("docs/intro.md", "---\ntitle: Intro\nslug: start\n---\n\nIntro body.\n");
        await Write("docs/guide/_category_.json", "{\"label\": \"Guide\"}\n");
        await Write("docs/guide/README.md", "---\ntitle: Guide\n---\n\nGuide body.\n");
        await Write("docs/guide/まず-はじめに.md", "---\ntitle: Hajime\n---\n\nUnicode body.\n");
        await Write("docs/guide/tabs.mdx", "---\ntitle: Tabs\n---\nimport Tabs from '@theme/Tabs';\nimport TabItem from '@theme/TabItem';\n\n<Tabs><TabItem value=\"a\" label=\"A\">Alpha</TabItem></Tabs>\n");
        await Write("docs/guide/custom.md", "---\ntitle: Custom\nslug: my-page\n---\n\nCustom body.\n");
        await Write("docs/api/docusaurus.config.js.mdx", "---\ntitle: Config API\nslug: /api/docusaurus-config\n---\n\nConfig API body.\n");
        await Write("docs/linkfix.mdx", "---\ntitle: Linkfix\n---\nimport Link from '@docusaurus/Link';\n\n<Link href=\"/mig/docs/next/start/\">Start</Link>\n");
        await Write("versioned_docs/version-1.0/intro.md", "---\ntitle: Old Intro\n---\n\nOld body.\n");
        await Write("versioned_sidebars/version-1.0-sidebars.json", "{\"docs\": [\"intro\"]}\n");
        await Write("blog/authors.yml", "ada:\n  name: Ada Lovelace\n");
        await Write("blog/2024-01-02-hello.md", "---\ntitle: Hello\nauthors: [ada]\nsummary: Hi\nimage: ./img/social.png\n---\n\nHello body.\n");
        await Write("blog/plain.md", "---\ntitle: Plain\ndate: 2024-03-04\nsummary: P\n---\n\nPlain body.\n");
        await Write("blog/2024/05-06-nested.md", "---\ntitle: Nested\nsummary: N\n---\n\nNested body.\n");
        await Write("i18n/ja/docusaurus-plugin-content-docs/current/intro.md", "---\ntitle: Ja Intro\n---\n\nJa body.\n");
        Directory.CreateDirectory(Path.Combine(site, "static/img"));
        await File.WriteAllBytesAsync(Path.Combine(site, "static/img/logo.png"), "PNG"u8.ToArray());
        await Write("src/components/Bad.js", "// @docusaurus/theme-foo\n");
        await Write("src/pages/about.md", "---\ntitle: About\n---\n\nAbout body.\n");
        return site;
    }

    private static IReadOnlyList<string> ExpectedRoutes() =>
    [
        "/mig/blog/2024/01/02/hello/",
        "/mig/blog/2024/03/04/plain/",
        "/mig/blog/2024/05/06/nested/",
        "/mig/docs/intro/",
        "/mig/docs/next/api/docusaurus-config/",
        "/mig/docs/next/guide/",
        "/mig/docs/next/guide/" + Uri.EscapeDataString("まず-はじめに") + "/",
        "/mig/docs/next/guide/my-page/",
        "/mig/docs/next/guide/tabs/",
        "/mig/docs/next/linkfix/",
        "/mig/docs/next/start/",
        "/mig/ja/docs/intro/",
    ];

    private static string WorkerDirectory()
    {
        for (var path = new DirectoryInfo(AppContext.BaseDirectory); path is not null; path = path.Parent)
        {
            if (File.Exists(Path.Combine(path.FullName, "LithoSharp.slnx")))
            {
                return Path.Combine(path.FullName, "src", "LithoSharp.Mdx", "worker");
            }
        }

        throw new DirectoryNotFoundException("The MDX integration fixture requires the repository's restored worker.");
    }

    [Test]
    public async Task DryRun_WritesNothingAndClassifies()
    {
        using var workspace = new TemporaryWorkspace();
        var site = await WriteSourceAsync(workspace.Root);
        var before = Directory.EnumerateFiles(site, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();

        var result = DocusaurusMigration.Analyze(site, ExpectedRoutes(), Options);

        await Assert.That(Directory.EnumerateFiles(site, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray())
            .IsEquivalentTo(before);
        await Assert.That(result.WroteOutput).IsFalse();
        await Assert.That(result.ExitCode).IsEqualTo(DocusaurusMigration.ExitUnconvertible);
        await Assert.That(result.MissingRoutes).IsEmpty();
        await Assert.That(result.ExtraRoutes).IsEmpty();

        DocusaurusMigrationVerdict Verdict(string path) =>
            result.Files.Single(file => file.SourcePath == path).Verdict;
        await Assert.That(Verdict("docs/intro.md")).IsEqualTo(DocusaurusMigrationVerdict.Automatic);
        await Assert.That(Verdict("docs/api/docusaurus.config.js.mdx")).IsEqualTo(DocusaurusMigrationVerdict.Convertible);
        await Assert.That(Verdict("docs/guide/_category_.json")).IsEqualTo(DocusaurusMigrationVerdict.Automatic);
        await Assert.That(Verdict("docs/guide/tabs.mdx")).IsEqualTo(DocusaurusMigrationVerdict.Automatic);
        await Assert.That(Verdict("docs/guide/README.md")).IsEqualTo(DocusaurusMigrationVerdict.Convertible);
        await Assert.That(Verdict("docs/guide/custom.md")).IsEqualTo(DocusaurusMigrationVerdict.Convertible);
        await Assert.That(Verdict("docs/linkfix.mdx")).IsEqualTo(DocusaurusMigrationVerdict.Convertible);
        await Assert.That(Verdict("versions.json")).IsEqualTo(DocusaurusMigrationVerdict.Convertible);
        await Assert.That(Verdict("versioned_sidebars/version-1.0-sidebars.json")).IsEqualTo(DocusaurusMigrationVerdict.Convertible);
        await Assert.That(Verdict("blog/authors.yml")).IsEqualTo(DocusaurusMigrationVerdict.Convertible);
        await Assert.That(Verdict("blog/2024-01-02-hello.md")).IsEqualTo(DocusaurusMigrationVerdict.Convertible);
        await Assert.That(Verdict("blog/plain.md")).IsEqualTo(DocusaurusMigrationVerdict.Convertible);
        await Assert.That(Verdict("blog/2024/05-06-nested.md")).IsEqualTo(DocusaurusMigrationVerdict.Convertible);
        await Assert.That(Verdict("static/img/logo.png")).IsEqualTo(DocusaurusMigrationVerdict.Automatic);
        await Assert.That(Verdict("docusaurus.config.js")).IsEqualTo(DocusaurusMigrationVerdict.ManualActionRequired);
        await Assert.That(Verdict("sidebars.js")).IsEqualTo(DocusaurusMigrationVerdict.ManualActionRequired);
        await Assert.That(Verdict("src/pages/about.md")).IsEqualTo(DocusaurusMigrationVerdict.ManualActionRequired);
        await Assert.That(Verdict("src/components/Bad.js")).IsEqualTo(DocusaurusMigrationVerdict.Unsupported);

        var bad = result.Files.Single(file => file.SourcePath == "src/components/Bad.js");
        await Assert.That(bad.Issues.Any(issue => issue.Id == "LSMIG003")).IsTrue();
        var config = result.Files.Single(file => file.SourcePath == "docusaurus.config.js");
        await Assert.That(config.ConvertedPath).IsNull();
        await Assert.That(config.Issues.Any(issue => issue.Id == "LSMIG001")).IsTrue();
    }

    [Test]
    public async Task Convert_WritesSeparateOutputWithPreservedRoutes()
    {
        using var workspace = new TemporaryWorkspace();
        var site = await WriteSourceAsync(workspace.Root);
        var destination = Path.Combine(workspace.Root, "converted");

        var result = await DocusaurusMigration.ConvertAsync(site, destination, ExpectedRoutes(), Options);

        await Assert.That(result.WroteOutput).IsTrue();
        await Assert.That(result.ExitCode).IsEqualTo(DocusaurusMigration.ExitUnconvertible);
        await Assert.That(result.MissingRoutes).IsEmpty();
        await Assert.That(result.ExtraRoutes).IsEmpty();
        // Existing inputs are never overwritten: the source tree gains nothing.
        await Assert.That(File.Exists(Path.Combine(site, "migration-manifest.json"))).IsFalse();

        var linkfix = await File.ReadAllTextAsync(Path.Combine(destination, "docs/linkfix.mdx"));
        await Assert.That(linkfix.Contains("@docusaurus/Link")).IsFalse();
        await Assert.That(linkfix).Contains("<Link href=\"/mig/docs/next/start/\">Start</Link>");
        var hello = await File.ReadAllTextAsync(Path.Combine(destination, "blog/2024-01-02-hello.md"));
        await Assert.That(hello).Contains("date: 2024-01-02");
        await Assert.That(hello).Contains("slug: 2024/01/02/hello");
        await Assert.That(hello).Contains("image: ./img/social.png");
        var plain = await File.ReadAllTextAsync(Path.Combine(destination, "blog/plain.md"));
        await Assert.That(plain).Contains("slug: 2024/03/04/plain");
        await Assert.That(File.Exists(Path.Combine(destination, "docs/guide/index.md"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(destination, "docs/guide/README.md"))).IsFalse();
        var custom = await File.ReadAllTextAsync(Path.Combine(destination, "docs/guide/custom.md"));
        await Assert.That(custom).Contains("slug: guide/my-page");

        var manifest = result.Manifest;
        DocusaurusMigrationVariant Variant(string version, string locale) =>
            manifest.Variants.Single(variant => variant.Version == version && variant.Locale == locale);
        await Assert.That(Variant("next", "en").InputDirectory).IsEqualTo("docs");
        await Assert.That(Variant("next", "en").SuggestedRoutePrefix).IsEqualTo("docs/next");
        await Assert.That(Variant("1.0", "en").InputDirectory).IsEqualTo("versioned_docs/version-1.0");
        await Assert.That(Variant("1.0", "en").SuggestedRoutePrefix).IsEqualTo("docs");
        await Assert.That(Variant("current", "ja").InputDirectory).IsEqualTo("i18n/ja/docusaurus-plugin-content-docs/current");
        await Assert.That(Variant("current", "ja").SuggestedRoutePrefix).IsEqualTo("ja/docs");
        await Assert.That(manifest.VersionedSidebars["1.0"]).IsEqualTo("versioned_sidebars/version-1.0-sidebars.json");
        await Assert.That(manifest.BlogAuthors.Single().Id).IsEqualTo("ada");
        await Assert.That(manifest.ManualSteps.Count > 0).IsTrue();
        await Assert.That(manifest.UnsupportedNotes.Any(note => note.Contains("Bad.js"))).IsTrue();

        // Deterministic reruns produce identical bytes.
        var again = Path.Combine(workspace.Root, "converted-again");
        _ = await DocusaurusMigration.ConvertAsync(site, again, ExpectedRoutes(), Options);
        foreach (var file in Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories))
        {
            var other = Path.Combine(again, Path.GetRelativePath(destination, file));
            await Assert.That(await File.ReadAllBytesAsync(other)).IsEquivalentTo(await File.ReadAllBytesAsync(file));
        }

        await Assert.That(async () => await DocusaurusMigration.ConvertAsync(site, destination, ExpectedRoutes(), Options))
            .Throws<IOException>();
        await Assert.That(async () => await DocusaurusMigration.ConvertAsync(site, Path.Combine(site, "nested"), ExpectedRoutes(), Options))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task ConvertedTree_Builds()
    {
        using var workspace = new TemporaryWorkspace();
        var site = await WriteSourceAsync(workspace.Root);
        var destination = Path.Combine(workspace.Root, "converted");
        var manifest = (await DocusaurusMigration.ConvertAsync(site, destination, ExpectedRoutes(), Options)).Manifest;

        await using var docs = new DocumentationSite(new(workspace.Root, WorkerDirectory()) { Cacheable = true });
        docs.AddCollection(new("guide", manifest.Variants
            .Select(variant => new DocumentVariant(variant.Version, variant.Locale,
                Path.Combine(destination, variant.InputDirectory), variant.SuggestedRoutePrefix)).ToArray())
        { UseMdx = true });
        docs.AddBlog(new MdxBlogCollection("news", Path.Combine(destination, "blog"), "blog")
        {
            Authors = new Dictionary<string, BlogAuthor> { ["ada"] = new("Ada Lovelace") },
        });
        var output = Path.Combine(workspace.Root, "out");
        // Blog dates act as publication starts, so the timestamp stays fixed after the fixture dates.
        var generation = await new SiteGenerator().GenerateWithOptionsAsync(
            new SiteSettings { BaseUrl = "https://example.test/mig/" }, [], output, clean: true,
            new() { Template = new DocsSiteTemplate() },
            new() { Extensions = [docs], BuildTimestamp = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero) }, CancellationToken.None);
        await Assert.That(generation.BuildReport.Diagnostics).IsEmpty();

        var expected = new[]
        {
            Path.Combine(output, "docs/next/start/index.html"),
            Path.Combine(output, "docs/next/guide/index.html"),
            Path.Combine(output, "docs/next/guide/tabs/index.html"),
            Path.Combine(output, "docs/next/linkfix/index.html"),
            Path.Combine(output, "docs/intro/index.html"),
            Path.Combine(output, "ja/docs/intro/index.html"),
            Path.Combine(output, "blog/2024/01/02/hello/index.html"),
            Path.Combine(output, "blog/2024/03/04/plain/index.html"),
            Path.Combine(output, "blog/2024/05/06/nested/index.html"),
        };
        await Assert.That(expected.Where(file => !File.Exists(file)).ToArray()).IsEmpty();
    }

    [Test]
    public async Task AliasedLinkImport_StaysManualAndKeepsImport()
    {
        using var workspace = new TemporaryWorkspace();
        var site = Path.Combine(workspace.Root, "site");
        Directory.CreateDirectory(Path.Combine(site, "docs"));
        await File.WriteAllTextAsync(Path.Combine(site, "docs/alias.mdx"),
            "---\ntitle: Alias\n---\nimport MyLink from '@docusaurus/Link';\n\n<MyLink href=\"/mig/docs/start/\">Start</MyLink>\n");

        var result = DocusaurusMigration.Analyze(site, null, Options);

        var file = result.Files.Single(item => item.SourcePath == "docs/alias.mdx");
        await Assert.That(file.Verdict).IsEqualTo(DocusaurusMigrationVerdict.ManualActionRequired);
        await Assert.That(file.Issues.Any(issue => issue.Id == "LSMIG004" && issue.Message.Contains("MyLink"))).IsTrue();

        var destination = Path.Combine(workspace.Root, "converted");
        _ = await DocusaurusMigration.ConvertAsync(site, destination, null, Options);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(destination, "docs/alias.mdx")))
            .Contains("@docusaurus/Link");
    }

    [Test]
    public async Task LinkToAttribute_RewritesToHref()
    {
        using var workspace = new TemporaryWorkspace();
        var site = Path.Combine(workspace.Root, "site");
        Directory.CreateDirectory(Path.Combine(site, "docs"));
        await File.WriteAllTextAsync(Path.Combine(site, "docs/link-to.mdx"),
            "---\ntitle: LinkTo\n---\nimport Link from '@docusaurus/Link';\n\n<Link to=\"/destination/\">Go</Link>\n");

        var analyzed = DocusaurusMigration.Analyze(site, null, Options);
        await Assert.That(analyzed.Files.Single(item => item.SourcePath == "docs/link-to.mdx").Verdict)
            .IsEqualTo(DocusaurusMigrationVerdict.Convertible);

        var destination = Path.Combine(workspace.Root, "converted");
        _ = await DocusaurusMigration.ConvertAsync(site, destination, null, Options);
        var converted = await File.ReadAllTextAsync(Path.Combine(destination, "docs/link-to.mdx"));
        await Assert.That(converted.Contains("@docusaurus/Link")).IsFalse();
        await Assert.That(converted).Contains("<Link href=\"/destination/\">Go</Link>");
    }

    [Test]
    public async Task ExplicitIndexSlug_IsPreserved()
    {
        using var workspace = new TemporaryWorkspace();
        var site = Path.Combine(workspace.Root, "site");
        Directory.CreateDirectory(Path.Combine(site, "docs"));
        await File.WriteAllTextAsync(Path.Combine(site, "docs/index.md"),
            "---\ntitle: Landing\nslug: /landing\n---\n\nLanding body.\n");

        var analyzed = DocusaurusMigration.Analyze(site, ["/mig/docs/landing/"], Options);
        await Assert.That(analyzed.MissingRoutes).IsEmpty();
        await Assert.That(analyzed.ExtraRoutes).IsEmpty();
        await Assert.That(analyzed.Files.Single(item => item.SourcePath == "docs/index.md").Verdict)
            .IsEqualTo(DocusaurusMigrationVerdict.Automatic);

        var destination = Path.Combine(workspace.Root, "converted");
        _ = await DocusaurusMigration.ConvertAsync(site, destination, ["/mig/docs/landing/"], Options);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(destination, "docs/index.md")))
            .Contains("slug: /landing");
    }

    [Test]
    public async Task NodeModulesAndBuildOutputs_AreExcluded()
    {
        using var workspace = new TemporaryWorkspace();
        var site = Path.Combine(workspace.Root, "site");
        Directory.CreateDirectory(Path.Combine(site, "docs"));
        await File.WriteAllTextAsync(Path.Combine(site, "docs/intro.md"), "---\ntitle: Intro\n---\n\nBody.\n");
        Directory.CreateDirectory(Path.Combine(site, "node_modules/sample"));
        await File.WriteAllTextAsync(Path.Combine(site, "node_modules/sample/package.json"), "{\"theme\": \"x\"}\n");
        Directory.CreateDirectory(Path.Combine(site, "docs/obj"));
        await File.WriteAllTextAsync(Path.Combine(site, "docs/obj/cached.json"), "{}\n");

        var result = DocusaurusMigration.Analyze(site, null, Options);

        await Assert.That(result.Files.Any(file => file.SourcePath.Contains("node_modules"))).IsFalse();
        await Assert.That(result.Files.Any(file => file.SourcePath.Contains("obj/"))).IsFalse();
        await Assert.That(result.Files.Single(file => file.SourcePath == "docs/intro.md").Verdict)
            .IsEqualTo(DocusaurusMigrationVerdict.Automatic);
    }

    [Test]
    public async Task CleanSource_ExitsClean()
    {
        using var workspace = new TemporaryWorkspace();
        var site = Path.Combine(workspace.Root, "site");
        Directory.CreateDirectory(Path.Combine(site, "docs"));
        await File.WriteAllTextAsync(Path.Combine(site, "docs/intro.md"), "---\ntitle: Intro\n---\n\nBody.\n");

        var analyzed = DocusaurusMigration.Analyze(site, null, Options);
        await Assert.That(analyzed.ExitCode).IsEqualTo(DocusaurusMigration.ExitClean);
        await Assert.That(analyzed.MissingRoutes).IsEmpty();
        await Assert.That(analyzed.ExtraRoutes).IsEmpty();

        var converted = await DocusaurusMigration.ConvertAsync(site, Path.Combine(workspace.Root, "out"), null, Options);
        await Assert.That(converted.ExitCode).IsEqualTo(DocusaurusMigration.ExitClean);
        await Assert.That(converted.WroteOutput).IsTrue();
    }

    [Test]
    public async Task BarePackageImport_NeedsManualInstallDecision()
    {
        using var workspace = new TemporaryWorkspace();
        var site = Path.Combine(workspace.Root, "site");
        Directory.CreateDirectory(Path.Combine(site, "docs"));
        await File.WriteAllTextAsync(Path.Combine(site, "docs/bare.mdx"),
            "---\ntitle: Bare\n---\nimport { Tweet } from \"react-tweet\";\nimport { useState } from \"react\";\n\n<Tweet id=\"1\" />\n\n```js\nimport { format } from \"prettier\";\n```\n");

        var analyzed = DocusaurusMigration.Analyze(site, ["/mig/docs/bare/"], Options);
        await Assert.That(analyzed.MissingRoutes).IsEmpty();
        await Assert.That(analyzed.ExtraRoutes).IsEmpty();
        var file = analyzed.Files.Single(item => item.SourcePath == "docs/bare.mdx");
        await Assert.That(file.Verdict).IsEqualTo(DocusaurusMigrationVerdict.ManualActionRequired);
        await Assert.That(file.Issues.Any(issue => issue.Id == "LSMIG004" && issue.Message.Contains("react-tweet"))).IsTrue();
        await Assert.That(file.Issues.Any(issue => issue.Message.Contains("prettier") || issue.Message.Contains("useState"))).IsFalse();
    }

    [Test]
    public async Task DeepTocMax_KeepsRangeNote()
    {
        using var workspace = new TemporaryWorkspace();
        var site = Path.Combine(workspace.Root, "site");
        Directory.CreateDirectory(Path.Combine(site, "docs"));
        await File.WriteAllTextAsync(Path.Combine(site, "docs/deep.md"),
            "---\ntitle: Deep\ntoc_max_heading_level: 5\n---\n\nBody.\n");
        await File.WriteAllTextAsync(Path.Combine(site, "docs/shallow.md"),
            "---\ntitle: Shallow\ntoc_max_heading_level: 3\n---\n\nBody.\n");

        var analyzed = DocusaurusMigration.Analyze(site, ["/mig/docs/deep/", "/mig/docs/shallow/"], Options);
        await Assert.That(analyzed.MissingRoutes).IsEmpty();
        await Assert.That(analyzed.ExtraRoutes).IsEmpty();
        var deep = analyzed.Files.Single(item => item.SourcePath == "docs/deep.md");
        await Assert.That(deep.Verdict).IsEqualTo(DocusaurusMigrationVerdict.ManualActionRequired);
        await Assert.That(deep.Issues.Any(issue => issue.Id == "LSMIG004" && issue.Message.Contains("h2-h3"))).IsTrue();
        var shallow = analyzed.Files.Single(item => item.SourcePath == "docs/shallow.md");
        await Assert.That(shallow.Issues.Any(issue => issue.Message.Contains("h2-h3"))).IsFalse();
    }

    [Test]
    public async Task MissingFrontMatter_DerivesTitleAndRoutes()
    {
        using var workspace = new TemporaryWorkspace();
        var site = Path.Combine(workspace.Root, "site");
        Directory.CreateDirectory(Path.Combine(site, "docs"));
        await File.WriteAllTextAsync(Path.Combine(site, "docs/plain.mdx"), "# Plain Title\n\nBody.\n");

        var analyzed = DocusaurusMigration.Analyze(site, ["/mig/docs/plain/"], Options);
        await Assert.That(analyzed.MissingRoutes).IsEmpty();
        await Assert.That(analyzed.ExtraRoutes).IsEmpty();
        var file = analyzed.Files.Single(item => item.SourcePath == "docs/plain.mdx");
        await Assert.That(file.Verdict).IsEqualTo(DocusaurusMigrationVerdict.Convertible);

        var destination = Path.Combine(workspace.Root, "converted");
        _ = await DocusaurusMigration.ConvertAsync(site, destination, ["/mig/docs/plain/"], Options);
        var converted = await File.ReadAllTextAsync(Path.Combine(destination, "docs/plain.mdx"));
        await Assert.That(converted.StartsWith("---\ntitle: Plain Title\n---\n", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task NestedDateDirectory_WithIndexFile_ResolvesDate()
    {
        using var workspace = new TemporaryWorkspace();
        var site = Path.Combine(workspace.Root, "site");
        Directory.CreateDirectory(Path.Combine(site, "blog", "2024", "05-07-colocated"));
        await File.WriteAllTextAsync(Path.Combine(site, "blog/2024/05-06-nested.md"),
            "---\ntitle: Nested\nsummary: N\n---\n\nNested body.\n");
        Directory.CreateDirectory(Path.Combine(site, "blog/2024/05-07-colocated"));
        await File.WriteAllTextAsync(Path.Combine(site, "blog/2024/05-07-colocated/index.md"),
            "---\ntitle: Colocated\nsummary: C\n---\n\nColocated body.\n");

        var analyzed = DocusaurusMigration.Analyze(site,
            ["/mig/blog/2024/05/06/nested/", "/mig/blog/2024/05/07/colocated/"], Options);
        await Assert.That(analyzed.MissingRoutes).IsEmpty();
        await Assert.That(analyzed.ExtraRoutes).IsEmpty();
    }

    [Test]
    public async Task NonDateDirectory_WithIndexFile_KeepsDirectoryRoute()
    {
        using var workspace = new TemporaryWorkspace();
        var site = Path.Combine(workspace.Root, "site");
        Directory.CreateDirectory(Path.Combine(site, "blog/releases/3.8"));
        await File.WriteAllTextAsync(Path.Combine(site, "blog/releases/3.8/index.mdx"),
            "---\ntitle: Release\ndate: 2025-05-26\nsummary: R\n---\n\nRelease body.\n");

        var analyzed = DocusaurusMigration.Analyze(site, ["/mig/blog/releases/3.8/"], Options);
        await Assert.That(analyzed.MissingRoutes).IsEmpty();
        await Assert.That(analyzed.ExtraRoutes).IsEmpty();
    }

    [Test]
    public async Task DuplicateExplicitIds_StayRoutedWithUniqueIds()
    {
        using var workspace = new TemporaryWorkspace();
        var site = Path.Combine(workspace.Root, "site");
        Directory.CreateDirectory(Path.Combine(site, "docs"));
        await File.WriteAllTextAsync(Path.Combine(site, "docs/first.md"),
            "---\ntitle: First\nid: shared\nslug: first\n---\n\nFirst body.\n");
        await File.WriteAllTextAsync(Path.Combine(site, "docs/second.md"),
            "---\ntitle: Second\nid: shared\nslug: second\n---\n\nSecond body.\n");

        var analyzed = DocusaurusMigration.Analyze(site, ["/mig/docs/first/", "/mig/docs/second/"], Options);
        await Assert.That(analyzed.MissingRoutes).IsEmpty();
        await Assert.That(analyzed.ExtraRoutes).IsEmpty();

        var destination = Path.Combine(workspace.Root, "converted");
        _ = await DocusaurusMigration.ConvertAsync(site, destination, ["/mig/docs/first/", "/mig/docs/second/"], Options);
        var second = await File.ReadAllTextAsync(Path.Combine(destination, "docs/second.md"));
        await Assert.That(second).Contains("id: shared-2");
    }

    [Test]
    public async Task DanglingCategoryLink_IsRemovedWithManualNote()
    {
        using var workspace = new TemporaryWorkspace();
        var site = Path.Combine(workspace.Root, "site");
        Directory.CreateDirectory(Path.Combine(site, "docs", "guide"));
        await File.WriteAllTextAsync(Path.Combine(site, "docs", "guide", "deep.md"),
            "---\ntitle: Deep\n---\n\nDeep body.\n");
        await File.WriteAllTextAsync(Path.Combine(site, "docs", "guide", "_category_.yml"),
            "label: Guide\nlink:\n  type: doc\n  id: guide/missing\n");
        await File.WriteAllTextAsync(Path.Combine(site, "docs", "plain.md"),
            "---\ntitle: Plain\n---\n\nPlain body.\n");

        var analyzed = DocusaurusMigration.Analyze(site, ["/mig/docs/guide/deep/", "/mig/docs/plain/"], Options);
        await Assert.That(analyzed.MissingRoutes).IsEmpty();
        await Assert.That(analyzed.ExtraRoutes).IsEmpty();
        var category = analyzed.Files.Single(item => item.SourcePath == "docs/guide/_category_.yml");
        await Assert.That(category.Verdict).IsEqualTo(DocusaurusMigrationVerdict.ManualActionRequired);

        var destination = Path.Combine(workspace.Root, "converted");
        _ = await DocusaurusMigration.ConvertAsync(site, destination, ["/mig/docs/guide/deep/", "/mig/docs/plain/"], Options);
        var converted = await File.ReadAllTextAsync(Path.Combine(destination, "docs", "guide", "_category_.yml"));
        await Assert.That(converted.Contains("missing")).IsFalse();
        await Assert.That(converted.Contains("label: Guide")).IsTrue();
    }

    [Test]
    public async Task KeyedInlineAuthor_CollapsesToProfileId()
    {
        using var workspace = new TemporaryWorkspace();
        var site = Path.Combine(workspace.Root, "site");
        Directory.CreateDirectory(Path.Combine(site, "blog"));
        await File.WriteAllTextAsync(Path.Combine(site, "blog/authors.yml"), "ada:\n  name: Ada Lovelace\n");
        await File.WriteAllTextAsync(Path.Combine(site, "blog/2024-06-07-keyed.md"),
            "---\ntitle: Keyed\ndate: 2024-06-07\nsummary: K\nauthors:\n  - key: ada\n    title: Guest\n  - ada\n---\n\nKeyed body.\n");

        var analyzed = DocusaurusMigration.Analyze(site, ["/mig/blog/2024/06/07/keyed/"], Options);
        await Assert.That(analyzed.MissingRoutes).IsEmpty();
        await Assert.That(analyzed.ExtraRoutes).IsEmpty();

        var destination = Path.Combine(workspace.Root, "converted");
        _ = await DocusaurusMigration.ConvertAsync(site, destination, ["/mig/blog/2024/06/07/keyed/"], Options);
        var converted = await File.ReadAllTextAsync(Path.Combine(destination, "blog/2024-06-07-keyed.md"));
        await Assert.That(converted.Contains("- ada")).IsTrue();
        await Assert.That(converted.Contains("key:")).IsFalse();
    }

    [Test]
    public async Task FencedImportSample_NeitherReportsNorDrops()
    {
        using var workspace = new TemporaryWorkspace();
        var site = Path.Combine(workspace.Root, "site");
        Directory.CreateDirectory(Path.Combine(site, "docs"));
        await File.WriteAllTextAsync(Path.Combine(site, "docs/sample.mdx"),
            "---\ntitle: Sample\n---\n\n```js\nimport Navbar from '@theme/Navbar';\n```\n");

        var analyzed = DocusaurusMigration.Analyze(site, ["/mig/docs/sample/"], Options);
        await Assert.That(analyzed.MissingRoutes).IsEmpty();
        await Assert.That(analyzed.ExtraRoutes).IsEmpty();
        var file = analyzed.Files.Single(item => item.SourcePath == "docs/sample.mdx");
        await Assert.That(file.Verdict).IsEqualTo(DocusaurusMigrationVerdict.Automatic);

        var destination = Path.Combine(workspace.Root, "converted");
        _ = await DocusaurusMigration.ConvertAsync(site, destination, ["/mig/docs/sample/"], Options);
        var converted = await File.ReadAllTextAsync(Path.Combine(destination, "docs/sample.mdx"));
        await Assert.That(converted.Contains("import Navbar from '@theme/Navbar';")).IsTrue();
    }

    [Test]
    public async Task MdxCodeBlockImport_IsExecutableAndReports()
    {
        using var workspace = new TemporaryWorkspace();
        var site = Path.Combine(workspace.Root, "site");
        Directory.CreateDirectory(Path.Combine(site, "docs"));
        await File.WriteAllTextAsync(Path.Combine(site, "docs/block.mdx"),
            "---\ntitle: Block\n---\n\n```mdx-code-block\nimport {useLocation} from '@docusaurus/router';\n```\n\nUse it.\n");

        var analyzed = DocusaurusMigration.Analyze(site, ["/mig/docs/block/"], Options);
        var file = analyzed.Files.Single(item => item.SourcePath == "docs/block.mdx");
        await Assert.That(file.Verdict).IsEqualTo(DocusaurusMigrationVerdict.ManualActionRequired);
        await Assert.That(file.Issues.Any(issue => issue.Message.Contains("@docusaurus/router"))).IsTrue();
    }

    [Test]
    public async Task MdxCodeBlockInsideOuterSample_StaysNonExecutable()
    {
        using var workspace = new TemporaryWorkspace();
        var site = Path.Combine(workspace.Root, "site");
        Directory.CreateDirectory(Path.Combine(site, "docs"));
        await File.WriteAllTextAsync(Path.Combine(site, "docs/outer.mdx"),
            "---\ntitle: Outer\n---\n\n````md\n```mdx-code-block\nimport {useLocation} from '@docusaurus/router';\n```\n````\n");

        var analyzed = DocusaurusMigration.Analyze(site, ["/mig/docs/outer/"], Options);
        var file = analyzed.Files.Single(item => item.SourcePath == "docs/outer.mdx");
        await Assert.That(file.Verdict).IsEqualTo(DocusaurusMigrationVerdict.Automatic);
    }

    [Test]
    public async Task ShimmedHookImport_IsManualNotUnsupported()
    {
        using var workspace = new TemporaryWorkspace();
        var site = Path.Combine(workspace.Root, "site");
        Directory.CreateDirectory(Path.Combine(site, "docs"));
        await File.WriteAllTextAsync(Path.Combine(site, "docs/hook.mdx"),
            "---\ntitle: Hook\n---\n\nimport {useActiveDocContext} from '@docusaurus/plugin-content-docs/client';\n\nUse it.\n");

        var analyzed = DocusaurusMigration.Analyze(site, ["/mig/docs/hook/"], Options);
        var file = analyzed.Files.Single(item => item.SourcePath == "docs/hook.mdx");
        await Assert.That(file.Verdict).IsEqualTo(DocusaurusMigrationVerdict.ManualActionRequired);
        await Assert.That(file.Issues.Any(issue => issue.Message.Contains("plugin-content-docs"))).IsTrue();
    }

    [Test]
    public async Task BlogFrontMatterKeys_MatchMdxBinder()
    {
        var actual = typeof(MdxBlogFrontMatter).GetProperties()
            .Select(property => System.Text.RegularExpressions.Regex.Replace(property.Name, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant())
            .Order(StringComparer.Ordinal).ToArray();
        await Assert.That(actual).IsEquivalentTo(DocusaurusMigration.BlogFrontMatterKeys.Order(StringComparer.Ordinal).ToArray());
    }
}
