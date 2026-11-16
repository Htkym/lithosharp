namespace LithoSharp.AgentReady;

/// <summary>The public Markdown provision outcome for one published HTML page. This is not an HTTP delivery check.</summary>
public sealed class PublicMarkdownPageResult
{
    internal PublicMarkdownPageResult(string htmlPath, string? markdownPath, PublicMarkdownStatus status, string? reason)
    {
        HtmlPath = htmlPath;
        MarkdownPath = markdownPath;
        Status = status;
        Reason = reason;
    }

    public string HtmlPath { get; }
    public string? MarkdownPath { get; }
    public PublicMarkdownStatus Status { get; }
    /// <summary>A public, sanitized explanation when complete Markdown was unavailable.</summary>
    public string? Reason { get; }
}
