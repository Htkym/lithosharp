using System.Text.Json;

namespace LithoSharp;

internal static class ThemeScript
{
    internal static string BrowserScript { get; } = Read();
    internal static string Restore(string basePath) => BrowserScript + "\n" +
        "globalThis[Symbol.for('lithosharp.theme')].restore(" + JsonSerializer.Serialize(basePath) + ");";
    internal static string Start(string basePath) => BrowserScript + "\n" +
        "globalThis[Symbol.for('lithosharp.theme')].start(" + JsonSerializer.Serialize(basePath) + ");";
    private static string Read()
    {
        using var reader = new StreamReader(typeof(ThemeScript).Assembly.GetManifestResourceStream("LithoSharp.Browser.theme-controller.mjs")!);
        return reader.ReadToEnd();
    }
}
