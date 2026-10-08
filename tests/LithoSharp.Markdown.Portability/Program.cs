using System.Collections;
using System.Reflection;
using System.Runtime.Versioning;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LithoSharp;

var product = typeof(SiteGenerator).Assembly;
var runtime = typeof(LithoSharp.Markdown.MarkdownParser).Assembly;
var runtimeTarget = runtime.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;
Check(runtimeTarget == ".NETCoreApp,Version=v10.0", "Product parser host must target net10.0.");
Check(product.GetType("LithoSharp.Content.Compilation.LithoBlockParser") is null,
    "Product must consume the shared runtime parser instead of compiling its own copy.");
var portableContext = new AssemblyLoadContext("MD-03 portable canary", isCollectible: true);
portableContext.Resolving += (_, name) => AssemblyLoadContext.Default.LoadFromAssemblyName(name);
var portable = portableContext.LoadFromAssemblyPath(Path.Combine(AppContext.BaseDirectory, "Portable", "LithoSharp.Markdown.dll"));
var portableTarget = portable.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;
Check(portableTarget == ".NETStandard,Version=v2.0", "Portable assembly must really target netstandard2.0.");
Check(!portable.GetReferencedAssemblies().Any(reference => reference.Name == "LithoSharp"),
    "The standalone parser must not reference the product assembly.");
Check(!portable.GetTypes().Any(type => type.Name is "LithoHtmlRenderer" or "SiteDiagnostic" or "SiteGenerator"),
    "Rendering/site types must remain outside the parse-only assembly.");

var cases = new (string Id, string Input)[]
{
    ("C01-body-basics", File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "body-basics.md"))),
    ("portable-reference-utf16-fence", "# A&amp;\\*😀\r\n\r\n> [ref][r]\r\n\r\n[r]: /docs?a=1&amp;b=2 \"Guide\"\r\n\r\n```csharp title=\"Case\" {1} showLineNumbers start=3\r\nx\r\n```\r\n"),
};

foreach (var (id, input) in cases)
{
    var runtimeTree = Parse(runtime, input);
    var portableTree = Parse(portable, input);
    var runtimeSnapshot = JsonSerializer.Serialize(Snapshot(runtimeTree));
    var portableSnapshot = JsonSerializer.Serialize(Snapshot(portableTree));
    Check(runtimeSnapshot == portableSnapshot, id + ": structural/span/reference mismatch.");
    if (id == "C01-body-basics")
    {
        var blocks = ((IEnumerable)runtimeTree.GetType().GetField("Item1")!.GetValue(runtimeTree)!).Cast<object>().ToArray();
        var levels = blocks.Where(block => block.GetType().Name == "LithoHeading")
            .Select(block => (int)block.GetType().GetProperty("Level")!.GetValue(block)!);
        Check(levels.SequenceEqual(new[] { 1, 2, 2 }), "Baseline raw heading levels changed.");
        Check(blocks.Count(block => block.GetType().Name == "LithoCode") == 1, "Baseline code fence changed.");
    }

    if (id == "portable-reference-utf16-fence")
    {
        var blocks = ((IEnumerable)runtimeTree.GetType().GetField("Item1")!.GetValue(runtimeTree)!).Cast<object>().ToArray();
        var code = blocks.Single(block => block.GetType().Name == "LithoCode");
        var meta = code.GetType().GetProperty("Meta")!.GetValue(code)!;
        Check((string?)meta.GetType().GetProperty("Title")!.GetValue(meta) == "Case"
            && (string?)meta.GetType().GetProperty("Highlight")!.GetValue(meta) == "1"
            && (bool)meta.GetType().GetProperty("ShowLineNumbers")!.GetValue(meta)!
            && (int)meta.GetType().GetProperty("StartLine")!.GetValue(meta)! == 3,
            "Portable regex fence metadata changed.");
        var references = (IDictionary)runtimeTree.GetType().GetField("Item2")!.GetValue(runtimeTree)!;
        var reference = references.Values.Cast<object>().Single();
        Check((string?)reference.GetType().GetProperty("Url")!.GetValue(reference) == "/docs?a=1&b=2",
            "Reference destination entity decoding changed.");
    }

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        id,
        rawUtf16Length = input.Length,
        runtimeTarget,
        portableTarget,
        snapshotSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(runtimeSnapshot))).ToLowerInvariant(),
        status = "PASS",
    }));
}

var generator = new SiteGenerator();
const string smallMarkdown = "## Shared\n\nBody text.";
var render = typeof(SiteGenerator).GetMethod("RenderMarkdown", BindingFlags.Instance | BindingFlags.NonPublic)!;
var html = (string)render.Invoke(generator, new object[] { smallMarkdown })!;
Check(html.Contains("<h2", StringComparison.Ordinal) && html.Contains("id=\"", StringComparison.Ordinal)
    && html.Contains("Body text.", StringComparison.Ordinal), "Existing compiler output changed.");
Check(html == (string)render.Invoke(generator, new object[] { smallMarkdown })!, "Existing compiler output is not deterministic.");
Check(!AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetName().Name == "Markdig"),
    "The normal product path must not load the isolated oracle.");
Console.WriteLine("PASS existing C01 compiler assertions / NoMarkdigAssemblyLoads");

MarkdownFactsChecks.Run();
Md02ReviewFixChecks.Run(runtime, portable);
Md03AdapterChecks.Run(product, runtime, portable);

static object Parse(Assembly assembly, string input)
{
    var parser = assembly.GetType("LithoSharp.Content.Compilation.LithoBlockParser", throwOnError: true)!;
    return parser.GetMethod("ParseBlocks", BindingFlags.Public | BindingFlags.Static)!
        .Invoke(null, new object[] { input, CancellationToken.None })!;
}

// Compare the internal node DTOs without widening the product's public API.
static object? Snapshot(object? value)
{
    if (value is null || value is string || value is bool || value is int) return value;
    if (value is IEnumerable sequence)
        return sequence.Cast<object?>().Select(Snapshot).ToArray();
    var type = value.GetType();
    var result = new SortedDictionary<string, object?>(StringComparer.Ordinal) { ["$type"] = type.Name };
    foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        if (property.GetIndexParameters().Length == 0) result[property.Name] = Snapshot(property.GetValue(value));
    foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        result[field.Name] = Snapshot(field.GetValue(value));
    return result;
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
