namespace LithoSharp;

internal static class DocsTocScript
{
    internal static string BrowserScript { get; } = Read();
    private static string Read()
    {
        using var reader = new StreamReader(typeof(DocsTocScript).Assembly.GetManifestResourceStream("LithoSharp.Browser.toc.mjs")!);
        return reader.ReadToEnd();
    }
}
