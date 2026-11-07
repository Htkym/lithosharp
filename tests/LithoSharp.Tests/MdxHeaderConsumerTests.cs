using LithoSharp.Content;
using LithoSharp.Diagnostics;
using LithoSharp.Mdx;
using LithoSharp.Pages;
using LithoSharp.Routing;

namespace LithoSharp.Tests;

public sealed class MdxHeaderConsumerTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task UnknownHeaderReturnsUnavailableWithoutStartingWorkerOrInventingFacts(bool oversized)
    {
        using var workspace = new TemporaryWorkspace();
        var started = Path.Combine(workspace.Root, "started.txt");
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "worker.mjs"),
            "import {writeFileSync} from 'node:fs';writeFileSync(process.env.LITHOSHARP_TEST_STARTED,'started');process.exit(23);");
        await using var session = new MdxInspectionSession(new(workspace.Root, workspace.Root)
        {
            Environment = new Dictionary<string, string> { ["LITHOSHARP_TEST_STARTED"] = started },
        });
        var text = oversized ? OversizedHeader() : "\uFEFF---\r\ntitle: \uD800\r\n---\r\n# Body\r\n";
        var result = await session.AnalyzeAsync("unknown.mdx", text, new(7, 3));

        await Assert.That(session.WorkerStarts).IsEqualTo(0);
        await Assert.That(File.Exists(started)).IsFalse();
        await Assert.That(result.SourcePath).IsEqualTo("unknown.mdx");
        await Assert.That(result.DocumentVersion).IsEqualTo(7);
        await Assert.That(result.ProjectGeneration).IsEqualTo(3);
        await Assert.That(result.Stage).IsEqualTo("unavailable");
        await Assert.That(result.Title).IsNull();
        await Assert.That(result.PlainText).IsEqualTo(string.Empty);
        await Assert.That(result.Headings).IsEmpty();
        await Assert.That(result.Links).IsEmpty();
        await Assert.That(result.Assets).IsEmpty();
        await Assert.That(result.Components).IsEmpty();
        await Assert.That(result.Imports).IsEmpty();
        var diagnostic = result.Diagnostics.Single();
        await Assert.That(diagnostic.Id).IsEqualTo("LSM011");
        await Assert.That(diagnostic.Severity).IsEqualTo(SiteDiagnosticSeverity.Error);
        await Assert.That(diagnostic.Message).Contains("front matter scanning is incomplete");
        await Assert.That(diagnostic.Location!.FilePath).IsEqualTo("unknown.mdx");
        if (oversized)
        {
            await Assert.That(diagnostic.Location.Line).IsNull();
            await Assert.That(diagnostic.Location.Column).IsNull();
        }
        else
        {
            await Assert.That(diagnostic.Location.Line).IsEqualTo(2);
            await Assert.That(diagnostic.Location.Column).IsEqualTo(8);
            await Assert.That(diagnostic.Location.EndColumn).IsEqualTo(9);
        }
    }

    [Test]
    public async Task ClosedHeaderPassesTheCompleteLargeBodyToTheWorker()
    {
        using var workspace = new TemporaryWorkspace();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "worker.mjs"), """
            import {createInterface} from 'node:readline';
            console.log(JSON.stringify({protocol:1,type:'ready',node:'24.13.0',mdx:'3.1.1',react:'19.2.4',esbuild:'0.28.2'}));
            const expected='\n\n\n# Large body\n\n'+'x'.repeat(1024*1024)+'\n';
            for await(const line of createInterface({input:process.stdin,crlfDelay:Infinity})){
              const request=JSON.parse(line);
              console.log(JSON.stringify({protocol:1,requestId:request.requestId,success:true,result:{
                headings:[],links:[],islands:[],imports:[],diagnostics:[],text:request.text===expected?'original-body':'altered-body'
              }}));
            }
            """);
        await using var session = new MdxInspectionSession(new(workspace.Root, workspace.Root)
        {
            Timeout = TimeSpan.FromSeconds(10),
        });
        var text = "---\ntitle: Large\n---\n# Large body\n\n" + new string('x', 1024 * 1024) + "\n";
        var result = await session.AnalyzeAsync("large-body.mdx", text);

        await Assert.That(session.WorkerStarts).IsEqualTo(1);
        await Assert.That(result.Stage).IsEqualTo("syntax");
        await Assert.That(result.Diagnostics).IsEmpty();
        await Assert.That(result.PlainText).IsEqualTo("original-body");
    }

    [Test]
    public async Task MdxLoaderRejectsUnknownHeaderWithTheSharedIncompleteDiagnostic()
    {
        using var workspace = new TemporaryWorkspace();
        var sourcePath = Path.Combine(workspace.Root, "unknown.mdx");
        await File.WriteAllTextAsync(sourcePath, OversizedHeader());
        var loader = new MdxContentCollectionLoader<MdxIntegrationTests.FrontMatter>(new("mdx"), workspace.Root,
            _ => SiteRoute.ForDirectoryIndex("page"), entry => new PageMetadata(entry.FrontMatter.Title));
        var result = await loader.LoadAsync();

        await Assert.That(result.IsSuccess).IsFalse();
        await Assert.That(result.Collection).IsNull();
        var diagnostic = result.Diagnostics.Single();
        await Assert.That(diagnostic.Id).IsEqualTo("LSM011");
        await Assert.That(diagnostic.Message).Contains("front matter scanning is incomplete");
        await Assert.That(diagnostic.Location!.FilePath).IsEqualTo("unknown.mdx");
        await Assert.That(diagnostic.Location.Line).IsNull();
        await Assert.That(diagnostic.Location.Column).IsNull();
    }

    private static string OversizedHeader() => "---\ntitle: " + new string('x', 1024 * 1024) + "\n---\n# Body\n";
}
