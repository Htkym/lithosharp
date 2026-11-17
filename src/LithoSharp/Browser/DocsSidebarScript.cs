namespace LithoSharp;

internal static class DocsSidebarScript
{
    internal static string BrowserScript { get; } = Read();
    private static string Read()
    {
        using var reader = new StreamReader(typeof(DocsSidebarScript).Assembly.GetManifestResourceStream("LithoSharp.Browser.sidebar.mjs")!);
        return reader.ReadToEnd();
    }
}
