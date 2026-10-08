using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using LithoSharp.Content.Compilation;

namespace LithoSharp.Markdown;

/// <summary>A half-open UTF-16 range in the complete caller-supplied text.</summary>
public readonly record struct RawSpan
{
    public RawSpan(int start, int length)
    {
        if (start < 0 || length < 0) throw new ArgumentOutOfRangeException(nameof(start));
        _ = checked(start + length);
        Start = start; Length = length;
    }
    public int Start { get; }
    public int Length { get; }
    public int End => checked(Start + Length);
    internal static RawSpan From(MdRawRange value) => new(value.Start, value.Length);
    internal static RawSpan? From(MdRawRange? value) => value.HasValue ? From(value.Value) : null;
}
public readonly record struct MarkdownLinePosition(int Line, int Column);

public enum MarkdownParseStatus { Complete, Partial, Failed }
public enum MarkdownCoverageState { Complete, Partial, Unknown }
public enum MarkdownFeatureState { Supported, Unsupported, Partial }
public enum MarkdownSegmentKind { Linear, Atomic, Unknown }
public enum MarkdownMappingPrecision { Exact, CoveringTokens, Partial, Unknown }
public enum MarkdownFrontMatterState { Absent, Parsed, Empty, Invalid, Unterminated, Unknown }
public enum MarkdownYamlKind { Mapping, Sequence, Scalar }
public enum MarkdownLinkKind { Inline, Autolink, ReferenceUse, ReferenceDefinition }
public enum MarkdownLinkResolution { Inline, ResolvedReference, UnresolvedReference }
public enum MarkdownDiagnosticSeverity { Information, Warning, Error }

/// <summary>Immutable parse-only SourceSegment facts; positions refer to the original UTF-16 input.</summary>
public sealed class MarkdownSourceSegment
{
    internal MarkdownSourceSegment(MdSourceSegment facts)
    {
        DecodedSpan = global::LithoSharp.Markdown.RawSpan.From(facts.DecodedSpan);
        RawSpan = global::LithoSharp.Markdown.RawSpan.From(facts.RawSpan);
        Kind = (MarkdownSegmentKind)facts.Kind;
    }
    public RawSpan DecodedSpan { get; }
    public RawSpan? RawSpan { get; }
    public MarkdownSegmentKind Kind { get; }
}

/// <summary>Immutable parse-only MappingResult facts; positions refer to the original UTF-16 input.</summary>
public sealed class MarkdownMappingResult
{
    internal MarkdownMappingResult(MdMappingResult facts)
    {
        RawFragments = Array.AsReadOnly(facts.RawFragments.Select(value => global::LithoSharp.Markdown.RawSpan.From(value)).ToArray());
        Precision = (MarkdownMappingPrecision)facts.Precision;
    }
    public IReadOnlyList<RawSpan> RawFragments { get; }
    public MarkdownMappingPrecision Precision { get; }
}

/// <summary>Immutable parse-only TextProjection facts; positions refer to the original UTF-16 input.</summary>
public sealed class MarkdownTextProjection
{
    internal MarkdownTextProjection(MdTextProjection facts)
    {
        Text = facts.Text;
        SourceSegments = Array.AsReadOnly(facts.SourceSegments.Select(value => new MarkdownSourceSegment(value)).ToArray());
    }
    public string? Text { get; }
    public IReadOnlyList<MarkdownSourceSegment> SourceSegments { get; }
    public MarkdownMappingResult Map(RawSpan decodedSpan)
    {
        var core = new MdTextProjection(Text, SourceSegments.Select(s => new MdSourceSegment(
            new MdRawRange(s.DecodedSpan.Start, s.DecodedSpan.Length),
            s.RawSpan.HasValue ? new MdRawRange(s.RawSpan.Value.Start, s.RawSpan.Value.Length) : null,
            (MdSegmentKind)s.Kind)).ToArray());
        return new MarkdownMappingResult(MdSourceMapping.Map(core, new MdRawRange(decodedSpan.Start, decodedSpan.Length)));
    }
}

/// <summary>Immutable parse-only YamlNode facts; positions refer to the original UTF-16 input.</summary>
public sealed class MarkdownYamlNode
{
    internal MarkdownYamlNode(MdYamlNode facts)
    {
        Kind = (MarkdownYamlKind)facts.Kind;
        RawSpan = global::LithoSharp.Markdown.RawSpan.From(facts.RawSpan);
        Scalar = new MarkdownTextProjection(facts.Scalar);
        Children = Array.AsReadOnly(facts.Children.Select(value => new MarkdownYamlNode(value)).ToArray());
    }
    public MarkdownYamlKind Kind { get; }
    public RawSpan? RawSpan { get; }
    public MarkdownTextProjection Scalar { get; }
    public IReadOnlyList<MarkdownYamlNode> Children { get; }
}

/// <summary>Immutable parse-only FrontMatter facts; positions refer to the original UTF-16 input.</summary>
public sealed class MarkdownFrontMatter
{
    internal MarkdownFrontMatter(MdFrontMatter facts)
    {
        State = (MarkdownFrontMatterState)facts.State;
        OpeningSpan = global::LithoSharp.Markdown.RawSpan.From(facts.OpeningSpan);
        YamlSpan = global::LithoSharp.Markdown.RawSpan.From(facts.YamlSpan);
        ClosingSpan = global::LithoSharp.Markdown.RawSpan.From(facts.ClosingSpan);
        Root = facts.Root is null ? null : new MarkdownYamlNode(facts.Root);
    }
    public MarkdownFrontMatterState State { get; }
    public RawSpan? OpeningSpan { get; }
    public RawSpan? YamlSpan { get; }
    public RawSpan? ClosingSpan { get; }
    public MarkdownYamlNode? Root { get; }
}

/// <summary>Immutable parse-only Heading facts; positions refer to the original UTF-16 input.</summary>
public sealed class MarkdownHeading
{
    internal MarkdownHeading(MdHeading facts)
    {
        LocalKey = facts.LocalKey;
        RawLevel = facts.RawLevel;
        Text = new MarkdownTextProjection(facts.Text);
        RawSpan = global::LithoSharp.Markdown.RawSpan.From(facts.RawSpan);
        TextRawSpan = global::LithoSharp.Markdown.RawSpan.From(facts.TextRawSpan);
        Anchor = facts.Anchor;
    }
    public string LocalKey { get; }
    public int RawLevel { get; }
    public MarkdownTextProjection Text { get; }
    public RawSpan RawSpan { get; }
    public RawSpan? TextRawSpan { get; }
    public string Anchor { get; }
}

/// <summary>Immutable parse-only Section facts; positions refer to the original UTF-16 input.</summary>
public sealed class MarkdownSection
{
    internal MarkdownSection(MdSection facts)
    {
        LocalKey = facts.LocalKey;
        ParentLocalKey = facts.ParentLocalKey;
        HeadingLocalKey = facts.HeadingLocalKey;
        DirectBodySpan = global::LithoSharp.Markdown.RawSpan.From(facts.DirectBodySpan);
        SubtreeSpan = global::LithoSharp.Markdown.RawSpan.From(facts.SubtreeSpan);
    }
    public string LocalKey { get; }
    public string? ParentLocalKey { get; }
    public string? HeadingLocalKey { get; }
    public RawSpan? DirectBodySpan { get; }
    public RawSpan? SubtreeSpan { get; }
}

/// <summary>Immutable parse-only Link facts; positions refer to the original UTF-16 input.</summary>
public sealed class MarkdownLink
{
    internal MarkdownLink(MdLink facts)
    {
        Kind = (MarkdownLinkKind)facts.Kind;
        Resolution = (MarkdownLinkResolution)facts.Resolution;
        Image = facts.Image;
        RawSpan = global::LithoSharp.Markdown.RawSpan.From(facts.RawSpan);
        Label = new MarkdownTextProjection(facts.Label);
        Target = facts.Target;
        Title = facts.Title;
        TargetRawSpan = global::LithoSharp.Markdown.RawSpan.From(facts.TargetRawSpan);
        ReferenceLabel = facts.ReferenceLabel;
    }
    public MarkdownLinkKind Kind { get; }
    public MarkdownLinkResolution Resolution { get; }
    public bool Image { get; }
    public RawSpan RawSpan { get; }
    public MarkdownTextProjection Label { get; }
    public string? Target { get; }
    public string? Title { get; }
    public RawSpan? TargetRawSpan { get; }
    public string? ReferenceLabel { get; }
}

/// <summary>Immutable parse-only Fence facts; positions refer to the original UTF-16 input.</summary>
public sealed class MarkdownFence
{
    internal MarkdownFence(MdFence facts)
    {
        RawSpan = global::LithoSharp.Markdown.RawSpan.From(facts.RawSpan);
        OpeningMarkerSpan = global::LithoSharp.Markdown.RawSpan.From(facts.OpeningMarkerSpan);
        InfoSpan = global::LithoSharp.Markdown.RawSpan.From(facts.InfoSpan);
        ContentSpan = global::LithoSharp.Markdown.RawSpan.From(facts.ContentSpan);
        ClosingMarkerSpan = global::LithoSharp.Markdown.RawSpan.From(facts.ClosingMarkerSpan);
        Closed = facts.Closed;
        Info = facts.Info;
        Content = new MarkdownTextProjection(facts.Content);
    }
    public RawSpan RawSpan { get; }
    public RawSpan OpeningMarkerSpan { get; }
    public RawSpan? InfoSpan { get; }
    public RawSpan ContentSpan { get; }
    public RawSpan? ClosingMarkerSpan { get; }
    public bool Closed { get; }
    public string? Info { get; }
    public MarkdownTextProjection Content { get; }
}

/// <summary>Immutable parse-only TextRegion facts; positions refer to the original UTF-16 input.</summary>
public sealed class MarkdownTextRegion
{
    internal MarkdownTextRegion(MdTextRegion facts)
    {
        Kind = facts.Kind;
        RawSpan = global::LithoSharp.Markdown.RawSpan.From(facts.RawSpan);
        Content = new MarkdownTextProjection(facts.Content);
    }
    public string Kind { get; }
    public RawSpan? RawSpan { get; }
    public MarkdownTextProjection Content { get; }
}

/// <summary>Immutable parse-only Diagnostic facts; positions refer to the original UTF-16 input.</summary>
public sealed class MarkdownDiagnostic
{
    internal MarkdownDiagnostic(MdDiagnostic facts)
    {
        Id = facts.Id;
        Severity = (MarkdownDiagnosticSeverity)facts.Severity;
        Message = facts.Message;
        RawSpan = global::LithoSharp.Markdown.RawSpan.From(facts.RawSpan);
        Origin = facts.Origin;
        Reason = facts.Reason;
    }
    public string Id { get; }
    public MarkdownDiagnosticSeverity Severity { get; }
    public string Message { get; }
    public RawSpan? RawSpan { get; }
    public string Origin { get; }
    public string Reason { get; }
}

/// <summary>Immutable parse-only Capability facts; positions refer to the original UTF-16 input.</summary>
public sealed class MarkdownCapability
{
    internal MarkdownCapability(MdCapability facts)
    {
        Feature = facts.Feature;
        State = (MarkdownFeatureState)facts.State;
    }
    public string Feature { get; }
    public MarkdownFeatureState State { get; }
}

/// <summary>Immutable parse-only Coverage facts; positions refer to the original UTF-16 input.</summary>
public sealed class MarkdownCoverage
{
    internal MarkdownCoverage(MdCoverage facts)
    {
        RawPositions = (MarkdownCoverageState)facts.RawPositions;
        DecodedMapping = (MarkdownCoverageState)facts.DecodedMapping;
        UnprojectedRegions = Array.AsReadOnly(facts.UnprojectedRegions.Select(value => new MarkdownTextRegion(value)).ToArray());
        Reasons = Array.AsReadOnly(facts.Reasons.Select(value => value).ToArray());
    }
    public MarkdownCoverageState RawPositions { get; }
    public MarkdownCoverageState DecodedMapping { get; }
    public IReadOnlyList<MarkdownTextRegion> UnprojectedRegions { get; }
    public IReadOnlyList<string> Reasons { get; }
}

/// <summary>Immutable parse-only DocumentFacts facts; positions refer to the original UTF-16 input.</summary>
public sealed class MarkdownDocument
{
    internal MarkdownDocument(MdDocumentFacts facts)
    {
        RawText = facts.RawText;
        ScopeId = facts.ScopeId;
        SourceId = facts.SourceId;
        SourceVersion = facts.SourceVersion;
        ParserVersion = facts.ParserVersion;
        OptionsHash = facts.OptionsHash;
        TextHash = facts.TextHash;
        Status = (MarkdownParseStatus)facts.Status;
        BodySpan = global::LithoSharp.Markdown.RawSpan.From(facts.BodySpan);
        FrontMatter = new MarkdownFrontMatter(facts.FrontMatter);
        Headings = Array.AsReadOnly(facts.Headings.Select(value => new MarkdownHeading(value)).ToArray());
        Sections = Array.AsReadOnly(facts.Sections.Select(value => new MarkdownSection(value)).ToArray());
        Links = Array.AsReadOnly(facts.Links.Select(value => new MarkdownLink(value)).ToArray());
        Fences = Array.AsReadOnly(facts.Fences.Select(value => new MarkdownFence(value)).ToArray());
        TextRegions = Array.AsReadOnly(facts.TextRegions.Select(value => new MarkdownTextRegion(value)).ToArray());
        Diagnostics = Array.AsReadOnly(facts.Diagnostics.Select(value => new MarkdownDiagnostic(value)).ToArray());
        Capabilities = Array.AsReadOnly(facts.Capabilities.Select(value => new MarkdownCapability(value)).ToArray());
        Coverage = new MarkdownCoverage(facts.Coverage);
    }
    private string RawText { get; }
    public string ScopeId { get; }
    public string SourceId { get; }
    public string? SourceVersion { get; }
    public string ParserVersion { get; }
    public string OptionsHash { get; }
    public string? TextHash { get; }
    public MarkdownParseStatus Status { get; }
    public RawSpan? BodySpan { get; }
    public MarkdownFrontMatter FrontMatter { get; }
    public IReadOnlyList<MarkdownHeading> Headings { get; }
    public IReadOnlyList<MarkdownSection> Sections { get; }
    public IReadOnlyList<MarkdownLink> Links { get; }
    public IReadOnlyList<MarkdownFence> Fences { get; }
    public IReadOnlyList<MarkdownTextRegion> TextRegions { get; }
    public IReadOnlyList<MarkdownDiagnostic> Diagnostics { get; }
    public IReadOnlyList<MarkdownCapability> Capabilities { get; }
    public MarkdownCoverage Coverage { get; }
    public string ContractVersion => "1.0";
    public string ProfileId => "lithosharp-markdown/1";
    public MarkdownLinePosition GetLinePosition(int rawOffset)
    {
        if (rawOffset < 0 || rawOffset > RawText.Length) throw new ArgumentOutOfRangeException(nameof(rawOffset));
        var line = 1; var column = 1;
        for (var i = 0; i < rawOffset; i++)
        {
            if (RawText[i] == '\r') { line++; column = 1; if (i + 1 < rawOffset && RawText[i + 1] == '\n') i++; }
            else if (RawText[i] == '\n') { if (i == 0 || RawText[i - 1] != '\r') line++; column = 1; }
            else column++;
        }
        return new MarkdownLinePosition(line, column);
    }
}

/// <summary>The six contract-v1 options, with defaults expanded before hashing.</summary>
public sealed class MarkdownParseOptions
{
    public MarkdownParseOptions(int maxInputUtf16 = 1048576, int maxOutputItems = 131072,
        int maxNestingDepth = 200, int maxScanUnits = 16777216,
        string profileId = "lithosharp-markdown/1", int optionsSchemaVersion = 1)
    {
        Core = new MdOptions(maxInputUtf16, maxOutputItems, maxNestingDepth, maxScanUnits, profileId, optionsSchemaVersion);
    }
    internal MdOptions Core { get; }
    public int MaxInputUtf16 => Core.MaxInputUtf16;
    public int MaxOutputItems => Core.MaxOutputItems;
    public int MaxNestingDepth => Core.MaxNestingDepth;
    public int MaxScanUnits => Core.MaxScanUnits;
    public string ProfileId => Core.ProfileId;
    public int OptionsSchemaVersion => Core.OptionsSchemaVersion;
}

/// <summary>Parses owned text without rendering, site resolution, file access or user code.</summary>
public static class MarkdownParser
{
    public static MarkdownDocument Parse(string rawText, string scopeId, string sourceId,
        string? sourceVersion = null, MarkdownParseOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var facts = MdParser.Parse(rawText, scopeId, sourceId, sourceVersion, options?.Core ?? new MdOptions(), cancellationToken);
        var result = new MarkdownDocument(facts);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }
}
