using LithoSharp.Content;

namespace LithoSharp.Tests;

public sealed class MarkdownPostReaderTests
{
    [Test]
    public async Task ReadAllAsync_ParsesFrontMatterAndOrdersNewestFirst()
    {
        using var workspace = new TemporaryWorkspace();
        var posts = Path.Combine(workspace.Root, "posts");
        Directory.CreateDirectory(posts);
        await File.WriteAllTextAsync(Path.Combine(posts, "old.md"), """
            ---
            title: "Old"
            date: "2026-05-29T00:00:00Z"
            summary: "old summary"
            tags:
              - dotnet-runtime
            sources:
              - type: feed
                name: Example
            ---

            # Old
            """);
        await File.WriteAllTextAsync(Path.Combine(posts, "new.md"), """
            ---
            title: "New"
            date: "2026-05-30T00:00:00Z"
            summary: "new summary"
            tags:
              - github-copilot
            sources:
              - type: github
                name: owner/repo
            ---

            # New
            """);

        var result = await new MarkdownPostReader().ReadAllAsync(posts);

        await Assert.That(result).Count().IsEqualTo(2);
        await Assert.That(result[0].FrontMatter.Title).IsEqualTo("New");
        await Assert.That(result[0].RelativeOutputPath).IsEqualTo("posts/new.html");
    }

    [Test]
    public async Task ReadAsync_RejectsMissingTitle()
    {
        using var workspace = new TemporaryWorkspace();
        var path = Path.Combine(workspace.Root, "post.md");
        await File.WriteAllTextAsync(path, """
            ---
            date: "2026-05-30T00:00:00Z"
            ---

            Body
            """);

        await Assert.That(async () => await new MarkdownPostReader().ReadAsync(path, workspace.Root))
            .Throws<InvalidOperationException>()
            .WithMessageContaining("missing title");
    }

    [Test]
    public async Task ReadAsync_ParsesDocsSidebarFrontMatter()
    {
        using var workspace = new TemporaryWorkspace();
        var path = Path.Combine(workspace.Root, "guide.md");
        await File.WriteAllTextAsync(path, """
            ---
            title: "Guide"
            date: "2026-05-30T00:00:00Z"
            sidebar_position: 3
            sidebar_label: "Read this first"
            ---

            Body
            """);

        var post = await new MarkdownPostReader().ReadAsync(path, workspace.Root);

        await Assert.That(post.FrontMatter.SidebarPosition).IsEqualTo(3);
        await Assert.That(post.FrontMatter.SidebarLabel).IsEqualTo("Read this first");
    }
}
