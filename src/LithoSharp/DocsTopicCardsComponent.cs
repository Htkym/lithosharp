using System.Text;
using LithoSharp.Content;

namespace LithoSharp;

internal static class DocsTopicCardsComponent
{
    internal static string RenderCore(SiteTemplateNavigationNode root)
        => RenderCore(DocsPresentationNode.FromLegacy(root));

    internal static string RenderCore(DocsPresentationNode root)
    {
        if (root.Children.Count == 0)
            return string.Empty;
        var body = new StringBuilder("<div class=\"docs-topic-cards\">\n");
        foreach (var node in root.Children)
        {
            body.AppendLine("<article class=\"docs-topic-card\">");
            body.Append("<h2>");
            AppendLabel(body, node);
            body.AppendLine("</h2>");
            var summary = node.Description;
            if (!string.IsNullOrWhiteSpace(summary))
                body.AppendLine($"<p>{Html.Encode(summary)}</p>");
            AppendChildren(body, node);
            body.AppendLine("</article>");
        }
        body.AppendLine("</div>");
        return body.ToString();
    }

    private static void AppendChildren(StringBuilder body, DocsPresentationNode parent)
    {
        if (parent.Children.Count == 0)
            return;
        body.AppendLine("<ul>");
        foreach (var node in parent.Children)
        {
            body.Append("<li>");
            AppendLabel(body, node);
            AppendChildren(body, node);
            body.AppendLine("</li>");
        }
        body.AppendLine("</ul>");
    }

    private static void AppendLabel(StringBuilder body, DocsPresentationNode node)
    {
        if (node.Url is null)
            body.Append(Html.Encode(node.Label));
        else
            body.Append($"<a href=\"{Html.Encode(node.Url)}\">{Html.Encode(node.Label)}</a>");
    }
}
