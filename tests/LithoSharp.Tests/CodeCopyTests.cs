using LithoSharp.Configuration;
using LithoSharp.Documentation;
using LithoSharp.Mdx;

namespace LithoSharp.Tests;

public sealed class CodeCopyTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CoreAndProgressiveDocumentationShipTheSharedCopyEnhancement(bool progressive)
    {
        using var workspace = new TemporaryWorkspace();
        var input = Path.Combine(workspace.Root, "docs");
        Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "intro.md"), "---\ntitle: Intro\n---\n```csharp\nvar value = 1;\n```\n");
        await using var docs = new DocumentationSite(new(workspace.Root)) { Browser = progressive ? new() { Search = false } : null };
        docs.AddCollection(new("guide", [new("v1", "en", input, "guide")]));
        var output = Path.Combine(workspace.Root, "output");
        await new SiteGenerator().GenerateWithOptionsAsync(new SiteSettings(), [], output, false,
            new() { Template = new DocsSiteTemplate() }, new() { Extensions = [docs] }, default);
        var scripts = Directory.GetFiles(output, "*.js", SearchOption.AllDirectories);
        await Assert.That(scripts.Any(file => File.ReadAllText(file).Contains("lithosharp.code-copy", StringComparison.Ordinal))).IsTrue();
        var html = await File.ReadAllTextAsync(Path.Combine(output, "guide", "intro", "index.html"));
        await Assert.That(html).Contains("<code class=\"language-csharp\">");
        await Assert.That(html).DoesNotContain("ls-code-copy");
    }
}
