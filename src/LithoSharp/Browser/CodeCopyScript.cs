namespace LithoSharp;

internal static class CodeCopyScript
{
    internal static string BrowserScript { get; } = Read();

    private static string Read()
    {
        using var reader = new StreamReader(typeof(CodeCopyScript).Assembly.GetManifestResourceStream("LithoSharp.Browser.code-copy.mjs")!);
        return reader.ReadToEnd();
    }
}
