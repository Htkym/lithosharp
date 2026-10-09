using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using System.Text.Json;
using LithoSharp.Inspection;

internal static class Md04CacheChecks
{
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static object Get(object target, string name) => target.GetType().GetProperty(name, All)!.GetValue(target)!;
    private static object Field(object target, string name) => target.GetType().GetField(name, All)!.GetValue(target)!;
    private static int Count(DocumentWorkspace workspace) => (int)Get(Field(workspace, "_markdownFacts"), "Count");
    private static long Bytes(DocumentWorkspace workspace) => (long)Get(Field(workspace, "_markdownFacts"), "RetainedBytes");

    private static async Task<object> Parse(DocumentWorkspace workspace, string id, string text, string? version,
        long revision = 1, long generation = 1, object? options = null, CancellationToken cancellation = default)
    {
        Task task;
        try
        {
            task = (Task)typeof(DocumentWorkspace).GetMethod("ParseMarkdownAsync", All)!
                .Invoke(workspace, new object?[] { id, text, version, revision, generation, options, cancellation })!;
        }
        catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException!).Throw(); throw; }
        await task;
        return Get(task, "Result");
    }
    private static void Clear(DocumentWorkspace workspace) => typeof(DocumentWorkspace).GetMethod("ClearMarkdownFacts", All)!.Invoke(workspace, null);
    private static async Task Cancelled(Task task)
    {
        try { await task; throw new InvalidOperationException("Old Markdown request was published"); }
        catch (OperationCanceledException) { }
    }
    private static object FirstHeading(object facts) => ((System.Collections.IEnumerable)Get(facts, "Headings")).Cast<object>().First();
    private static int HeadingStart(object facts) => (int)Get(Get(FirstHeading(facts), "RawSpan"), "Start");

    internal static async Task Run(Assembly product, Assembly runtime)
    {
        await using var workspace = new DocumentWorkspace();
        await using var secondWorkspace = new DocumentWorkspace();
        const string raw = "# A\n";
        var first = await Parse(workspace, "a", raw, null);
        var hit = await Parse(workspace, "a", raw, null);
        var otherDocument = await Parse(workspace, "b", raw, null);
        var otherScope = await Parse(secondWorkspace, "a", raw, null);
        Check(ReferenceEquals(first, hit) && !ReferenceEquals(first, otherDocument) && !ReferenceEquals(first, otherScope),
            "Identity-bound cache: hit/a different document/a different workspace");
        Check((string)Get(first, "SourceId") == "a" && (string)Get(otherDocument, "SourceId") == "b"
            && (string)Get(first, "ScopeId") != (string)Get(otherScope, "ScopeId"), "Facts retain their own identity");
        var emptyVersion = await Parse(workspace, "a", raw, "");
        Check(!ReferenceEquals(first, emptyVersion) && (string)Get(emptyVersion, "SourceVersion") == "", "Null and empty source versions are distinct");
        var moved = await Parse(workspace, "a", "\n# A\n", "new", revision: 2);
        Check((string)Get(first, "TextHash") != (string)Get(moved, "TextHash")
            && HeadingStart(first) == 0 && HeadingStart(moved) == 1, "Span-only raw edit cannot reuse old positions");
        var reloaded = await Parse(workspace, "a", "\n# A\n", "new", revision: 0, generation: 2);
        Check(ReferenceEquals(moved, reloaded), "Syntax can be reused in a newer project generation without site resolution");
        await Cancelled(Parse(workspace, "a", raw, "older", revision: 99, generation: 1));

        var before = Count(workspace);
        var partial = await Parse(workspace, "partial", "<Widget>\n", "p");
        var failed = await Parse(workspace, "failed", "\uD800", "f");
        Check(Get(partial, "Status").ToString() == "Partial" && Get(failed, "Status").ToString() == "Failed"
            && Count(workspace) == before, "Partial and Failed facts are not cached");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Cancelled(Parse(workspace, "cancelled", raw, null, cancellation: cancelled.Token));
        Check(Count(workspace) == before, "Cancellation does not populate cache");

        // No sleeps or large input: block the two shared slots to make reservation races deterministic.
        var slots = (SemaphoreSlim)Field(workspace, "_analysisSlots");
        await slots.WaitAsync(); await slots.WaitAsync();
        var old = Parse(workspace, "race", "# Old\n", "old", revision: 1);
        var newer = Parse(workspace, "race", "# New\n", "new", revision: 2);
        slots.Release(2);
        await Cancelled(old); var newest = await newer;
        Check((string)Get(newest, "SourceVersion") == "new", "New reservation rejects delayed old publication");
        await slots.WaitAsync(); await slots.WaitAsync();
        var removed = Parse(workspace, "removed", raw, null);
        workspace.Remove("removed"); slots.Release(2); await Cancelled(removed);
        await slots.WaitAsync(); await slots.WaitAsync();
        var cleared = Parse(workspace, "cleared", raw, null);
        Clear(workspace); slots.Release(2); await Cancelled(cleared);
        Check(Count(workspace) == 0 && Bytes(workspace) == 0, "Remove/Clear epoch prevents resurrection and clears retained facts");

        for (var i = 0; i < 64; i++) await Parse(workspace, "lru-" + i, raw, null);
        var recent = await Parse(workspace, "lru-0", raw, null);
        await Parse(workspace, "lru-64", raw, null);
        Check(Count(workspace) == 64 && Bytes(workspace) <= 16 * 1024 * 1024, "Default cache remains bounded after65 documents");
        Check(ReferenceEquals(recent, await Parse(workspace, "lru-0", raw, null)), "LRU retains the touched document");
        var sizeType = product.GetType("LithoSharp.Inspection.MarkdownLogicalSize", true)!;
        var size = (long)sizeType.GetMethod("Measure", All)!.Invoke(null, new[] { first, (object)CancellationToken.None })!;
        var cacheType = product.GetType("LithoSharp.Inspection.MarkdownFactCache", true)!;
        var tooSmall = Activator.CreateInstance(cacheType, All, null, new object[] { 2, size - 1 }, null)!;
        cacheType.GetMethod("Publish", All)!.Invoke(tooSmall, new[] { first, (object)size });
        Check((int)Get(tooSmall, "Count") == 0 && (long)Get(tooSmall, "RetainedBytes") == 0, "Oversize entry is not retained");
        var byteBound = Activator.CreateInstance(cacheType, All, null, new object[] { 64, size }, null)!;
        cacheType.GetMethod("Publish", All)!.Invoke(byteBound, new[] { first, (object)size });
        var sizeOther = (long)sizeType.GetMethod("Measure", All)!.Invoke(null, new[] { otherDocument, (object)CancellationToken.None })!;
        cacheType.GetMethod("Publish", All)!.Invoke(byteBound, new[] { otherDocument, (object)sizeOther });
        Check((int)Get(byteBound, "Count") == 1 && (long)Get(byteBound, "RetainedBytes") <= size, "Byte cap independently evicts at fewer than64 entries");

        // The generic cache is independent of legacy inspection and its required-frontmatter policy.
        var beforeRemoveBytes = Bytes(workspace);
        workspace.Remove("lru-2");
        Check(Count(workspace) == 63 && Bytes(workspace) < beforeRemoveBytes, "Remove releases a retained generic document");
        var retainedCount = Count(workspace);
        var site = await workspace.InspectVersionedAsync("legacy.md", raw, 1, 1);
        Check(site.Diagnostics.Any(d => d.Id == "LSM001") && Count(workspace) == retainedCount, "Legacy site results never enter the strict facts cache");
        await workspace.DisposeAsync();
        Check(Count(workspace) == 0 && Bytes(workspace) == 0 && HeadingStart(first) == 0, "Dispose releases retention without rewriting returned facts");

        var identity = product.GetType("Syntamark.Hosting.MarkdownYamlIdentity", true)!;
        identity.GetMethod("RequirePinned", All)!.Invoke(null, null);
        var require = identity.GetMethod("Require", All)!;
        try
        {
            require.Invoke(null, new object[] { "18.1.0", "YamlDotNet, Version=99.0.0.0", "18.1.0" });
            throw new InvalidOperationException("Mismatched YAML identity was accepted");
        }
        catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException) { }
        var host = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(AppContext.BaseDirectory, "Host", "LithoSharp.Generators.dll"));
        var hostAdapter = host.GetType("LithoSharp.Generators.MarkdownSourceHostAdapter", true)!;
        var verify = hostAdapter.GetMethod("Verify", All)!;
        var parserVersion = (string)Get(first, "ParserVersion");
        const string yamlIdentity = "YamlDotNet, Version=18.0.0.0, Culture=neutral, PublicKeyToken=ec19458f3c15af5e";
        verify.Invoke(null, new[] { parserVersion, "1.0", "lithosharp-markdown/1", "18.1.0", yamlIdentity, "18.1.0" });
        Check(!host.GetReferencedAssemblies().Any(a => a.Name == "LithoSharp" || a.Name == "Syntamark"),
            "Prepared netstandard host contract does not reference the net10 facade");
        Console.WriteLine(JsonSerializer.Serialize(new { task = "MD-04", status = "PASS",
            checks = "identity/raw positions/LRU-entry-byte limits/Partial-Failed-cancel exclusion/generation reservation/Remove-Clear-Dispose/legacy separation/loaded YAML metadata",
            parserVersion, preparedHostContract = "netstandard2.0 metadata only", actualSourceArtifactRoslynHost = "NOT_RUN; MD-05/IN-01" }));
    }
    private static void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
}
