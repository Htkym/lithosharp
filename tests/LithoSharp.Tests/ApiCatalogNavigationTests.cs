using LithoSharp.Configuration;
using LithoSharp.Documentation;

namespace LithoSharp.Tests;

public sealed class ApiCatalogNavigationTests
{
    [Test]
    public async Task IndexGroupsTypesAndKeepsMembersOnTheirExistingPages()
    {
        using var workspace = new TemporaryWorkspace();
        var ids = new[] { "T:Example.Build.Builder", "M:Example.Build.Builder.#ctor", "M:Example.Build.Builder.Run(System.String)", "M:Example.Build.Builder.Run(System.Int32)", "P:Example.Build.Builder.Name", "T:Example.Build.Builder.Options", "P:Example.Build.Builder.Options.Enabled", "T:Example.Content.Page", "M:Orphan.Run", "T:Example.Build.Builder.Options.Advanced", "T:Example.Build.Builder.Options.Advanced.Limits", "P:Example.Build.Builder.Options.Advanced.Limits.Maximum" };
        var xml = Path.Combine(workspace.Root, "api.xml");
        await File.WriteAllTextAsync(xml, "<doc><members>" + string.Concat(ids.Select(id => "<member name=\"" + id + "\"><summary>Summary &amp; detail.</summary></member>")) + "</members></doc>");
        var api = new ApiReferenceSite();
        api.AddXml(new("library", new("v1", "ja", workspace.Root, "api"), xml));
        var output = Path.Combine(workspace.Root, "out");
        await new SiteGenerator().GenerateWithOptionsAsync(new SiteSettings { BaseUrl = "https://example.test/sub/" }, [], output, false,
            new() { Template = new DocsSiteTemplate() }, new() { Extensions = [api] }, default);
        var index = await File.ReadAllTextAsync(Path.Combine(output, "api", "index.html"));
        await Assert.That(index).Contains("Example.Build</h2>");
        await Assert.That(index).Contains("api-category-grid");
        await Assert.That(index).DoesNotContain("api-category-navigation");
        await Assert.That(index).DoesNotContain("api-member-table");
        await Assert.That(index).Contains("Example.Content</h2>");
        await Assert.That(index).Contains("<code>Builder</code>");
        await Assert.That(index).DoesNotContain("<code>Run(");
        await Assert.That(index).Contains("Orphan.Run");
        var type = await File.ReadAllTextAsync(Path.Combine(output, ApiReferenceSite.RouteFor("api", ids[0]).RelativeOutputPath));
        await Assert.That(type).Contains("コンストラクター");
        await Assert.That(type).Contains("プロパティ");
        await Assert.That(type).Contains("メソッド");
        await Assert.That(type).Contains("Run(System.String)");
        await Assert.That(type).Contains("Run(System.Int32)");
        await Assert.That(type).DoesNotContain("Options.Enabled");
        await Assert.That(type).Contains("Summary &amp; detail.");
        foreach (var id in ids)
            await Assert.That(File.Exists(Path.Combine(output, ApiReferenceSite.RouteFor("api", id).RelativeOutputPath))).IsTrue();
        var method = await File.ReadAllTextAsync(Path.Combine(output, ApiReferenceSite.RouteFor("api", ids[2]).RelativeOutputPath));
        await Assert.That(method).Contains("api-breadcrumbs");
        await Assert.That(method).Contains("data-docs-sidebar");
        await Assert.That(method).Contains("docs-nav-topic\" open");
        await Assert.That(method).Contains("aria-current=\"page\"");
        await Assert.That(method).Contains("<summary>Example.Build</summary>");
        var deep = await File.ReadAllTextAsync(Path.Combine(output, ApiReferenceSite.RouteFor("api", ids[^1]).RelativeOutputPath));
        await Assert.That(deep).Contains("<summary>Builder</summary>");
        await Assert.That(deep).Contains("<summary>Options</summary>");
        await Assert.That(deep).Contains("<summary>Advanced</summary>");
        await Assert.That(deep).Contains("<summary>Limits</summary>");
        await Assert.That(deep.Split("docs-nav-topic\" open", StringSplitOptions.None).Length >= 6).IsTrue();
        await Assert.That(method).Contains(ApiReferenceSite.RouteFor("api", ids[0], "https://example.test/sub/").PublicPath);
    }
}
