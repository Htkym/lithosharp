using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace LithoSharp.HtmlPortability;

internal sealed record HtmlBridgeInput(string Id, string Html, bool Scripting = false,
    string? ContextName = null, string? ContextNamespace = null, int? MaxInputChars = null, int? MaxNodes = null);

// Test-only driver shared across processes. It loads no parser source or runtime reference.
internal static class HtmlBridgeObservations
{
    internal static List<HtmlBridgeInput> Inputs(string corpusPath)
    {
        using var corpus = JsonDocument.Parse(File.ReadAllText(corpusPath));
        var inputs = corpus.RootElement.GetProperty("cases").EnumerateArray().Select(c =>
            new HtmlBridgeInput(c.GetProperty("id").GetString()!, c.GetProperty("html").GetString()!,
                c.GetProperty("scripting").GetBoolean(), c.GetProperty("mode").GetString() == "fragment"
                    ? c.GetProperty("context").GetString() : null)).ToList();
        inputs.AddRange(new[]
        {
            new HtmlBridgeInput("raw-entity-position", "<!doctype html>\r\n😀<A HREF=' HTTP://Example.COM/%2f/?x=&amp;amp;&y=e\u0301#Ä '>x</A><a href='&notit='></a><img src='&#x80;&#0;'>"),
            new HtmlBridgeInput("namespace-duplicate", "<!doctype html><svg><a XLINK:HREF='a&amp;b' href='plain' HREF='ignored'/><foreignObject><A HREF='h&#x2f;é'></A></foreignObject></svg><math><annotation-xml encoding='text/html'><a href='math-html'>x</a></annotation-xml></math>"),
            new HtmlBridgeInput("inert-off", "<template><a href='inert'>x</a></template><script>\"<a href='script'>\"</script><noscript><a href='fallback'>x</a></noscript><a href='active'>x</a>"),
            new HtmlBridgeInput("inert-on", "<template><a href='inert'>x</a></template><script>\"<a href='script'>\"</script><noscript><a href='fallback'>x</a></noscript><a href='active'>x</a>", Scripting: true),
            new HtmlBridgeInput("url-attributes", "<!doctype html><base href='/root/'><form action='submit'><button formaction='override'>Go</button></form><video poster='p.png' src='v.mp4'></video><object data='d.bin'></object><img srcset='a.png 1x, b.png 2x'>"),
            new HtmlBridgeInput("unsupported-shadow", "<a href='before'>x</a><template shadowrootmode='open'><a href='hidden'>x</a></template>"),
            new HtmlBridgeInput("unsupported-foreign", "<p><a href='x'>X</a>", ContextName: "svg", ContextNamespace: "http://www.w3.org/2000/svg"),
            new HtmlBridgeInput("failed-input-budget", "<a href='x'>X</a>", MaxInputChars: 3),
            new HtmlBridgeInput("partial-node-budget", "<a href='x'>X</a>", MaxNodes: 2)
        });
        return inputs;
    }

    internal static object Parse(Assembly assembly, HtmlBridgeInput input, CancellationToken cancellation = default)
    {
        Type Type(string name) => assembly.GetType("LithoSharp.HtmlParsing." + name, throwOnError: true)!;
        object? fragment = input.ContextName is null ? null : Activator.CreateInstance(Type("HtmlFragmentContext"),
            input.ContextName, input.ContextNamespace ?? "http://www.w3.org/1999/xhtml", null);
        var options = Activator.CreateInstance(Type("HtmlTreeOptions"), input.Scripting, fragment);
        var limits = Activator.CreateInstance(Type("HtmlTreeLimits"))!;
        if (input.MaxNodes is int nodes) limits.GetType().GetProperty("MaxNodes")!.SetValue(limits, nodes);
        if (input.MaxInputChars is int chars)
        {
            var tokenizer = limits.GetType().GetProperty("Tokenizer")!.GetValue(limits)!;
            tokenizer.GetType().GetProperty("MaxInputChars")!.SetValue(tokenizer, chars);
        }
        try { return Type("HtmlLiteralFacts").GetMethod("Parse", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new[] { input.Html, options, limits, cancellation })!; }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }

    internal static JsonElement Observe(Assembly assembly, HtmlBridgeInput input) => JsonSerializer.SerializeToElement(Parse(assembly, input));
}
