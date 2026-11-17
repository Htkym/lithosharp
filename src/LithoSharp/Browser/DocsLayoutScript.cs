namespace LithoSharp;

internal static class DocsLayoutScript
{
    internal static string BrowserScript { get; } = Read();
    private static string Read()
    {
        using var reader = new StreamReader(typeof(DocsLayoutScript).Assembly.GetManifestResourceStream("LithoSharp.Browser.layout.mjs")!);
        return reader.ReadToEnd();
    }
}

