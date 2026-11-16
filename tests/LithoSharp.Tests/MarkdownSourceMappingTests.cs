using System.Text;
using LithoSharp.Content;
using LithoSharp.Content.Compilation;
using LithoSharp.Diagnostics;
using LithoSharp.Quality;
using Syntamark.Compilation;

namespace LithoSharp.Tests;

public sealed class MarkdownSourceMappingTests
{
    [Test]
    public async Task IncludedRegionMapsExactTargetAndFollowingOriginalWithDirectiveContext()
    {
        using var workspace = new TemporaryWorkspace();
        var codePath = "code/cafe\u0301.cs";
        Directory.CreateDirectory(Path.Combine(workspace.Root, "code"));
        const string code = "\uFEFF#region Demo\r\nalpha 😀\r\n#endregion\r\n";
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, codePath), code, new UTF8Encoding(false));
        var body = "Before\r\n\r\n```cs source=\"" + codePath + "\" region=\"Demo\"\r\nplaceholder\r\n```\r\n\r\nAfter\r\n";
        var raw = "\uFEFF---\r\ntitle: Guide\r\n---\r\n" + body;
        var offset = raw.Length - body.Length;
        var bodyLine = new SourceText(raw).GetLineAndColumn(offset).Line;
        var inclusion = await MarkdownCodeInclusion.ResolveAsync(raw, body, workspace.Root, workspace.Root, "guide.md");
        var source = MarkdownSourceMapping.CreateSource(inclusion, "guide.md", inclusion.Body, offset, bodyLine)!;

        await Assert.That(inclusion.Diagnostics.Count).IsEqualTo(0);
        var target = source.MapDiagnostic(At(inclusion.Body, "alpha 😀", bodyLine));
        await Assert.That(target.Location!.FilePath).IsEqualTo(codePath);
        await Assert.That(target.Location.Line).IsEqualTo(2);
        await Assert.That(target.Location.Column).IsEqualTo(1);
        await Assert.That(target.Location.EndColumn).IsEqualTo("alpha 😀".Length + 1);
        await Assert.That(target.MappingPrecision).IsEqualTo("Exact");
        await Assert.That(target.RelatedLocations.Single().FilePath).IsEqualTo("guide.md");
        await Assert.That(target.RelatedLocations.Single().Line).IsEqualTo(bodyLine + 2);

        var after = source.MapDiagnostic(At(inclusion.Body, "After", bodyLine));
        var expected = new SourceText(raw).GetLineAndColumn(raw.IndexOf("After", StringComparison.Ordinal));
        await Assert.That(after.Location!.FilePath).IsEqualTo("guide.md");
        await Assert.That(after.Location.Line).IsEqualTo(expected.Line);
        await Assert.That(after.Location.Column).IsEqualTo(expected.Column);
        await Assert.That(after.MappingPrecision).IsEqualTo("Exact");
        await Assert.That(source.CanMapBody(inclusion.Body + "edited", "guide.md")).IsFalse();

        var trimmed = MarkdownSourceMapping.CreateSource(inclusion, "guide.md", inclusion.Body.Trim(), offset, bodyLine)!;
        var trimmedTarget = trimmed.MapDiagnostic(At(inclusion.Body.Trim(), "alpha 😀", bodyLine));
        await Assert.That(trimmedTarget.Location!.Line).IsEqualTo(2);
        await Assert.That(trimmedTarget.Location.FilePath).IsEqualTo(codePath);
        var report = new SiteQualityReport([trimmedTarget]);
        using var json = System.Text.Json.JsonDocument.Parse(report.Format(SiteDiagnosticFormat.Json));
        await Assert.That(json.RootElement.GetProperty("diagnostics")[0].GetProperty("mappingPrecision").GetString()).IsEqualTo("Exact");
        using var sarif = System.Text.Json.JsonDocument.Parse(report.Format(SiteDiagnosticFormat.Sarif));
        await Assert.That(sarif.RootElement.GetProperty("runs")[0].GetProperty("results")[0]
            .GetProperty("properties").GetProperty("mappingPrecision").GetString()).IsEqualTo("Exact");
    }

    [Test]
    public async Task HeaderAndLeadingTrimUseDirectOffsetsWithoutRetainingAFullSourceMap()
    {
        using var workspace = new TemporaryWorkspace();
        const string body = " \r\n   # Heading\r\nText";
        const string header = "---\r\ntitle: Guide\r\n---\r\n";
        var raw = header + body;
        var inclusion = await MarkdownCodeInclusion.ResolveAsync(raw, body, workspace.Root, workspace.Root, "guide.md");
        var bodyLine = new SourceText(raw).GetLineAndColumn(header.Length).Line;
        var source = MarkdownSourceMapping.CreateSource(inclusion, "guide.md", body.Trim(), header.Length, bodyLine)!;
        var mapped = source.MapDiagnostic(At(body.Trim(), "Heading", source.BodyStartLine));
        var expected = new SourceText(raw).GetLineAndColumn(raw.IndexOf("Heading", StringComparison.Ordinal));

        await Assert.That(source.SourceMap).IsNull();
        await Assert.That(source.CanMapBody(body.Trim(), "guide.md")).IsTrue();
        await Assert.That(mapped.Location!.Line).IsEqualTo(expected.Line);
        await Assert.That(mapped.Location.Column).IsEqualTo(expected.Column);
        await Assert.That(source.BodyStartOffset).IsEqualTo(raw.IndexOf('#'));
    }

    [Test]
    public async Task InsertedLineBreakHasUnknownOriginAndDoesNotInventRawPosition()
    {
        using var workspace = new TemporaryWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "sample.cs"), "alpha", new UTF8Encoding(false));
        const string body = "```cs source=\"sample.cs\"\nold\n```\n";
        var inclusion = await MarkdownCodeInclusion.ResolveAsync(body, body, workspace.Root, workspace.Root, "guide.md");
        var source = MarkdownSourceMapping.CreateSource(inclusion, "guide.md", inclusion.Body, 0, 1)!;
        var start = inclusion.Body.IndexOf("alpha", StringComparison.Ordinal) + "alpha".Length;
        var text = new SourceText(inclusion.Body);
        var from = text.GetLineAndColumn(start);
        var end = text.GetLineAndColumn(start + 1);
        var diagnostic = new SiteDiagnostic("TEST001", SiteDiagnosticSeverity.Warning, "Generated newline",
            new SiteSourceLocation("guide.md", from.Line, from.Column, end.Line, end.Column));
        var mapped = source.MapDiagnostic(diagnostic);

        await Assert.That(mapped.MappingPrecision).IsEqualTo("Unknown");
        await Assert.That(mapped.Location!.FilePath).IsEqualTo("guide.md");
        await Assert.That(mapped.Location.Line).IsNull();
        await Assert.That(mapped.RelatedLocations.Single().Line).IsEqualTo(1);
    }

    private static SiteDiagnostic At(string body, string token, int bodyLine)
    {
        var text = new SourceText(body);
        var start = body.IndexOf(token, StringComparison.Ordinal);
        var from = text.GetLineAndColumn(start);
        var to = text.GetLineAndColumn(start + token.Length);
        return new SiteDiagnostic("TEST001", SiteDiagnosticSeverity.Warning, token,
            new SiteSourceLocation("guide.md", bodyLine + from.Line - 1, from.Column,
                bodyLine + to.Line - 1, to.Column));
    }
}
