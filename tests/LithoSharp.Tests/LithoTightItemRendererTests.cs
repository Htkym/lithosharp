using LithoSharp.Content.Compilation;
using Syntamark.Compilation;

namespace LithoSharp.Tests;

public sealed class LithoTightItemRendererTests
{
    [Test]
    public async Task TightItemsPreserveEmptyChildrenAndParagraphLineFeeds()
    {
        LithoBlock[] blocks =
        [
            new LithoList(false, 1,
            [
                new LithoItem([], null, default),
                new LithoItem([new LithoParagraph([new LithoText("tail\n", default)], default), null!], null, default)
            ], true, default)
        ];

        await Assert.That(LithoHtmlRenderer.RenderHtml(blocks, []))
            .IsEqualTo("<ul>\n<li></li>\n<li>tail\n\n\n</li>\n</ul>\n");
    }

    [Test]
    public async Task TightBlockChildrenShareHeadingTraversalWithFollowingBlocks()
    {
        LithoBlock[] blocks =
        [
            new LithoList(false, 1,
            [
                new LithoItem(
                [
                    new LithoHeading(2, [new LithoText("inside", default)], default),
                    new LithoQuote([new LithoHeading(3, [new LithoText("nested", default)], default)], default)
                ], null, default)
            ], true, default),
            new LithoHeading(2, [new LithoText("after", default)], default)
        ];

        await Assert.That(LithoHtmlRenderer.RenderHtml(blocks, ["first", "nested", "last"]))
            .IsEqualTo("<ul>\n<li><h2 id=\"first\">inside</h2>\n"
                + "<blockquote>\n<h3 id=\"nested\">nested</h3>\n</blockquote>\n"
                + "</li>\n</ul>\n<h2 id=\"last\">after</h2>\n");
    }

    [Test]
    public async Task TightParagraphNullInlinesKeepTheArgumentName()
    {
        LithoBlock[] blocks =
        [
            new LithoList(false, 1,
                [new LithoItem([new LithoParagraph(null!, default)], null, default)], true, default)
        ];
        ArgumentNullException? failure = null;
        try
        {
            _ = LithoHtmlRenderer.RenderHtml(blocks, []);
        }
        catch (ArgumentNullException exception)
        {
            failure = exception;
        }

        await Assert.That(failure).IsNotNull();
        await Assert.That(failure!.ParamName).IsEqualTo("inlines");
    }
}
