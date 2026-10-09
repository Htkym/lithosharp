using System.Text;

namespace LithoSharp.HtmlParsing;

internal sealed class HtmlTextBuilder(int emptyOffset, int maxLength, Action<int, bool> reserve)
{
    private readonly StringBuilder value = new();
    private readonly List<HtmlTextSegment> segments = [];
    public int Length => value.Length;

    public void Append(string text, HtmlSpan source)
    {
        if (text.Length == 0) return;
        if (text.Length > maxLength - value.Length) throw new HtmlTokenizerBudgetException("value-budget");
        var canMerge = segments.Count != 0 && segments[^1].Source.End == source.Start &&
            segments[^1].ValueLength == segments[^1].Source.Length && text.Length == source.Length;
        reserve(text.Length, !canMerge);
        if (canMerge)
        {
            var last = segments[^1];
            segments[^1] = last with
            {
                ValueLength = last.ValueLength + text.Length,
                Source = new HtmlSpan(last.Source.Start, last.Source.Length + source.Length)
            };
        }
        else segments.Add(new HtmlTextSegment(value.Length, text.Length, source));
        value.Append(text);
    }

    public HtmlText Build()
    {
        var span = segments.Count == 0 ? new HtmlSpan(emptyOffset, 0) :
            new HtmlSpan(segments[0].Source.Start, segments[^1].Source.End - segments[0].Source.Start);
        return new HtmlText(value.ToString(), span, Array.AsReadOnly(segments.ToArray()));
    }
}

internal sealed class HtmlTokenizerBudgetException(string code) : Exception
{
    public string Code { get; } = code;
}
