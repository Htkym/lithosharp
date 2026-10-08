namespace LithoSharp.Content.Compilation;

/// <summary>Parser limits shared by runtime and source hosts.</summary>
internal static partial class LithoLimits
{
    /// <summary>Maximum nested container depth (blockquotes/lists). Deeper input degrades to paragraphs.</summary>
    public const int MaxNestingDepth = 200;

    /// <summary>Maximum reference label length, per CommonMark.</summary>
    public const int MaxReferenceLabelLength = 999;
}
