using System.Text.Json;
using System.Text.RegularExpressions;
using LithoSharp.Documentation;

namespace LithoSharp.Tests;

/// <summary>
/// C09: the Docusaurus profile is the single source of truth. Reference
/// versions match the pinned baseline fixture, the import and static lists
/// match the worker allowlists, and every construct named by the plan has
/// exactly one judgment. Unverified constructs stay unsupported.
/// </summary>
public sealed class DocusaurusProfileTests
{
    private static string RepoRoot()
    {
        for (var path = new DirectoryInfo(AppContext.BaseDirectory); path is not null; path = path.Parent)
        {
            if (File.Exists(Path.Combine(path.FullName, "LithoSharp.slnx")))
            {
                return path.FullName;
            }
        }

        throw new DirectoryNotFoundException("The Docusaurus profile fixture requires the repository root.");
    }

    private static IReadOnlyList<string> WorkerList(string fileName, string constant)
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LithoSharp.Mdx", "worker", fileName));
        var match = Regex.Match(source, $@"const {constant} = \[(.*?)\];", RegexOptions.Singleline);
        if (!match.Success)
        {
            throw new InvalidOperationException($"The worker no longer declares '{constant}' for the profile to agree with.");
        }

        return Regex.Matches(match.Groups[1].Value, @"'([^']+)'").Select(item => item.Groups[1].Value).ToArray();
    }

    [Test]
    public async Task ReferenceVersions_MatchBaselineFixture()
    {
        using var baseline = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(RepoRoot(), "tests", "fixtures", "mdx-baseline", "package.json")));
        var root = baseline.RootElement;
        var dependencies = root.GetProperty("dependencies");
        await Assert.That(dependencies.GetProperty("@docusaurus/core").GetString()).IsEqualTo(DocusaurusProfile.DocusaurusVersion);
        await Assert.That(dependencies.GetProperty("@mdx-js/mdx").GetString()).IsEqualTo(DocusaurusProfile.MdxVersion);
        await Assert.That(dependencies.GetProperty("react").GetString()).IsEqualTo(DocusaurusProfile.ReactVersion);
        await Assert.That(dependencies.GetProperty("esbuild").GetString()).IsEqualTo(DocusaurusProfile.EsbuildVersion);
        await Assert.That(root.GetProperty("engines").GetProperty("node").GetString()).IsEqualTo(DocusaurusProfile.NodeVersion);
        await Assert.That(root.GetProperty("packageManager").GetString()).IsEqualTo("npm@" + DocusaurusProfile.NpmVersion);
    }

    [Test]
    public async Task SupportedThemeImports_MatchWorkerAllowlist()
    {
        var worker = WorkerList("compiler.mjs", "supportedThemeComponents");
        await Assert.That(string.Join(",", worker)).IsEqualTo(string.Join(",", DocusaurusProfile.SupportedThemeImports));
    }

    [Test]
    public async Task BuiltInStaticComponents_MatchWorkerList()
    {
        var worker = WorkerList("compiler.mjs", "staticMdxComponents");
        await Assert.That(string.Join(",", worker)).IsEqualTo(string.Join(",", DocusaurusProfile.BuiltInStaticComponents));
    }

    [Test]
    public async Task RequiredItems_HaveExactlyOneJudgment()
    {
        var required = new[] { "Tabs", "TabItem", "Admonition", "Details", "CodeBlock", "TOCInline", "Link", "BrowserOnly", "useBaseUrl", "useDocusaurusContext", "Translate" };
        foreach (var name in required)
        {
            await Assert.That(DocusaurusProfile.Components.Count(item => item.Name == name)).IsEqualTo(1);
        }

        // Every worker-accepted import has a judgment: no supported surface stays unjudged.
        foreach (var name in DocusaurusProfile.SupportedThemeImports)
        {
            await Assert.That(DocusaurusProfile.Components.Count(item => item.Name == name)).IsEqualTo(1);
        }
    }

    [Test]
    public async Task Hooks_StayUnsupported()
    {
        foreach (var name in new[] { "useBaseUrl", "useDocusaurusContext" })
        {
            var judged = DocusaurusProfile.Components.Single(item => item.Name == name);
            await Assert.That(judged.Level).IsEqualTo(DocusaurusSupportLevel.Unsupported);
            await Assert.That(judged.Import).IsNull();
        }
    }

    [Test]
    public async Task IsSupportedImport_ClassifiesLikeTheWorker()
    {
        await Assert.That(DocusaurusProfile.IsSupportedImport("@docusaurus/BrowserOnly")).IsTrue();
        foreach (var name in DocusaurusProfile.SupportedThemeImports)
        {
            await Assert.That(DocusaurusProfile.IsSupportedImport("@theme/" + name)).IsTrue();
        }

        foreach (var name in new[] { "@theme/Layout", "@theme/MDXContent", "@docusaurus/Link", "@docusaurus/Translate", "@docusaurus/useBaseUrl", "@docusaurus/useDocusaurusContext", "@docusaurus/core", "@site/docs/intro", "@theme/tabs", null, "" })
        {
            await Assert.That(DocusaurusProfile.IsSupportedImport(name)).IsFalse();
        }
    }
}
