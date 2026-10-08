using System;
using System.Collections.Generic;
using System.Linq;

namespace LithoSharp.Content.Compilation;

internal readonly record struct MdRawRange
{
    internal MdRawRange(int start, int length)
    {
        if (start < 0 || length < 0) throw new ArgumentOutOfRangeException(nameof(start));
        _ = checked(start + length);
        Start = start; Length = length;
    }
    internal int Start { get; }
    internal int Length { get; }
    internal int End => checked(Start + Length);
}

internal static class MdFreeze
{
    internal static IReadOnlyList<T> Copy<T>(IEnumerable<T> values) => Array.AsReadOnly(values.ToArray());
    internal static IReadOnlyList<T> Empty<T>() => Array.AsReadOnly(new T[0]);
}

internal enum MdParseStatus { Complete, Partial, Failed }
internal enum MdCoverageState { Complete, Partial, Unknown }
internal enum MdFeatureState { Supported, Unsupported, Partial }
internal enum MdSegmentKind { Linear, Atomic, Unknown }
internal enum MdMappingPrecision { Exact, CoveringTokens, Partial, Unknown }
internal enum MdFrontMatterState { Absent, Parsed, Empty, Invalid, Unterminated, Unknown }
internal enum MdYamlKind { Mapping, Sequence, Scalar }
internal enum MdLinkKind { Inline, Autolink, ReferenceUse, ReferenceDefinition }
internal enum MdLinkResolution { Inline, ResolvedReference, UnresolvedReference }
internal enum MdDiagnosticSeverity { Information, Warning, Error }

internal sealed class MdSourceSegment
{
    internal MdSourceSegment(MdRawRange decodedSpan, MdRawRange? rawSpan, MdSegmentKind kind)
    {
        DecodedSpan = decodedSpan;
        RawSpan = rawSpan;
        Kind = kind;
    }
    internal MdRawRange DecodedSpan { get; }
    internal MdRawRange? RawSpan { get; }
    internal MdSegmentKind Kind { get; }
}

internal sealed class MdMappingResult
{
    internal MdMappingResult(IReadOnlyList<MdRawRange> rawFragments, MdMappingPrecision precision)
    {
        RawFragments = MdFreeze.Copy(rawFragments);
        Precision = precision;
    }
    internal IReadOnlyList<MdRawRange> RawFragments { get; }
    internal MdMappingPrecision Precision { get; }
}

internal sealed class MdTextProjection
{
    internal MdTextProjection(string? text, IReadOnlyList<MdSourceSegment> sourceSegments)
    {
        Text = text;
        SourceSegments = MdFreeze.Copy(sourceSegments);
    }
    internal string? Text { get; }
    internal IReadOnlyList<MdSourceSegment> SourceSegments { get; }
}

internal sealed class MdYamlNode
{
    internal MdYamlNode(MdYamlKind kind, MdRawRange? rawSpan, MdTextProjection scalar, IReadOnlyList<MdYamlNode> children)
    {
        Kind = kind;
        RawSpan = rawSpan;
        Scalar = scalar;
        Children = MdFreeze.Copy(children);
    }
    internal MdYamlKind Kind { get; }
    internal MdRawRange? RawSpan { get; }
    internal MdTextProjection Scalar { get; }
    internal IReadOnlyList<MdYamlNode> Children { get; }
}

internal sealed class MdFrontMatter
{
    internal MdFrontMatter(MdFrontMatterState state, MdRawRange? openingSpan, MdRawRange? yamlSpan, MdRawRange? closingSpan, MdYamlNode? root)
    {
        State = state;
        OpeningSpan = openingSpan;
        YamlSpan = yamlSpan;
        ClosingSpan = closingSpan;
        Root = root;
    }
    internal MdFrontMatterState State { get; }
    internal MdRawRange? OpeningSpan { get; }
    internal MdRawRange? YamlSpan { get; }
    internal MdRawRange? ClosingSpan { get; }
    internal MdYamlNode? Root { get; }
}

internal sealed class MdHeading
{
    internal MdHeading(string localKey, int rawLevel, MdTextProjection text, MdRawRange rawSpan, MdRawRange? textRawSpan, string anchor)
    {
        LocalKey = localKey;
        RawLevel = rawLevel;
        Text = text;
        RawSpan = rawSpan;
        TextRawSpan = textRawSpan;
        Anchor = anchor;
    }
    internal string LocalKey { get; }
    internal int RawLevel { get; }
    internal MdTextProjection Text { get; }
    internal MdRawRange RawSpan { get; }
    internal MdRawRange? TextRawSpan { get; }
    internal string Anchor { get; }
}

internal sealed class MdSection
{
    internal MdSection(string localKey, string? parentLocalKey, string? headingLocalKey, MdRawRange? directBodySpan, MdRawRange? subtreeSpan)
    {
        LocalKey = localKey;
        ParentLocalKey = parentLocalKey;
        HeadingLocalKey = headingLocalKey;
        DirectBodySpan = directBodySpan;
        SubtreeSpan = subtreeSpan;
    }
    internal string LocalKey { get; }
    internal string? ParentLocalKey { get; }
    internal string? HeadingLocalKey { get; }
    internal MdRawRange? DirectBodySpan { get; }
    internal MdRawRange? SubtreeSpan { get; }
}

internal sealed class MdLink
{
    internal MdLink(MdLinkKind kind, MdLinkResolution resolution, bool image, MdRawRange rawSpan, MdTextProjection label, string? target, string? title, MdRawRange? targetRawSpan, string? referenceLabel)
    {
        Kind = kind;
        Resolution = resolution;
        Image = image;
        RawSpan = rawSpan;
        Label = label;
        Target = target;
        Title = title;
        TargetRawSpan = targetRawSpan;
        ReferenceLabel = referenceLabel;
    }
    internal MdLinkKind Kind { get; }
    internal MdLinkResolution Resolution { get; }
    internal bool Image { get; }
    internal MdRawRange RawSpan { get; }
    internal MdTextProjection Label { get; }
    internal string? Target { get; }
    internal string? Title { get; }
    internal MdRawRange? TargetRawSpan { get; }
    internal string? ReferenceLabel { get; }
}

internal sealed class MdFence
{
    internal MdFence(MdRawRange rawSpan, MdRawRange openingMarkerSpan, MdRawRange? infoSpan, MdRawRange contentSpan, MdRawRange? closingMarkerSpan, bool closed, string? info, MdTextProjection content)
    {
        RawSpan = rawSpan;
        OpeningMarkerSpan = openingMarkerSpan;
        InfoSpan = infoSpan;
        ContentSpan = contentSpan;
        ClosingMarkerSpan = closingMarkerSpan;
        Closed = closed;
        Info = info;
        Content = content;
    }
    internal MdRawRange RawSpan { get; }
    internal MdRawRange OpeningMarkerSpan { get; }
    internal MdRawRange? InfoSpan { get; }
    internal MdRawRange ContentSpan { get; }
    internal MdRawRange? ClosingMarkerSpan { get; }
    internal bool Closed { get; }
    internal string? Info { get; }
    internal MdTextProjection Content { get; }
}

internal sealed class MdTextRegion
{
    internal MdTextRegion(string kind, MdRawRange? rawSpan, MdTextProjection content)
    {
        Kind = kind;
        RawSpan = rawSpan;
        Content = content;
    }
    internal string Kind { get; }
    internal MdRawRange? RawSpan { get; }
    internal MdTextProjection Content { get; }
}

internal sealed class MdDiagnostic
{
    internal MdDiagnostic(string id, MdDiagnosticSeverity severity, string message, MdRawRange? rawSpan, string origin, string reason)
    {
        Id = id;
        Severity = severity;
        Message = message;
        RawSpan = rawSpan;
        Origin = origin;
        Reason = reason;
    }
    internal string Id { get; }
    internal MdDiagnosticSeverity Severity { get; }
    internal string Message { get; }
    internal MdRawRange? RawSpan { get; }
    internal string Origin { get; }
    internal string Reason { get; }
}

internal sealed class MdCapability
{
    internal MdCapability(string feature, MdFeatureState state)
    {
        Feature = feature;
        State = state;
    }
    internal string Feature { get; }
    internal MdFeatureState State { get; }
}

internal sealed class MdCoverage
{
    internal MdCoverage(MdCoverageState rawPositions, MdCoverageState decodedMapping, IReadOnlyList<MdTextRegion> unprojectedRegions, IReadOnlyList<string> reasons)
    {
        RawPositions = rawPositions;
        DecodedMapping = decodedMapping;
        UnprojectedRegions = MdFreeze.Copy(unprojectedRegions);
        Reasons = MdFreeze.Copy(reasons);
    }
    internal MdCoverageState RawPositions { get; }
    internal MdCoverageState DecodedMapping { get; }
    internal IReadOnlyList<MdTextRegion> UnprojectedRegions { get; }
    internal IReadOnlyList<string> Reasons { get; }
}

internal sealed class MdDocumentFacts
{
    internal MdDocumentFacts(string rawText, string scopeId, string sourceId, string? sourceVersion, string parserVersion, string optionsHash, string? textHash, MdParseStatus status, MdRawRange? bodySpan, MdFrontMatter frontMatter, IReadOnlyList<MdHeading> headings, IReadOnlyList<MdSection> sections, IReadOnlyList<MdLink> links, IReadOnlyList<MdFence> fences, IReadOnlyList<MdTextRegion> textRegions, IReadOnlyList<MdDiagnostic> diagnostics, IReadOnlyList<MdCapability> capabilities, MdCoverage coverage)
    {
        RawText = rawText;
        ScopeId = scopeId;
        SourceId = sourceId;
        SourceVersion = sourceVersion;
        ParserVersion = parserVersion;
        OptionsHash = optionsHash;
        TextHash = textHash;
        Status = status;
        BodySpan = bodySpan;
        FrontMatter = frontMatter;
        Headings = MdFreeze.Copy(headings);
        Sections = MdFreeze.Copy(sections);
        Links = MdFreeze.Copy(links);
        Fences = MdFreeze.Copy(fences);
        TextRegions = MdFreeze.Copy(textRegions);
        Diagnostics = MdFreeze.Copy(diagnostics);
        Capabilities = MdFreeze.Copy(capabilities);
        Coverage = coverage;
    }
    internal string RawText { get; }
    internal string ScopeId { get; }
    internal string SourceId { get; }
    internal string? SourceVersion { get; }
    internal string ParserVersion { get; }
    internal string OptionsHash { get; }
    internal string? TextHash { get; }
    internal MdParseStatus Status { get; }
    internal MdRawRange? BodySpan { get; }
    internal MdFrontMatter FrontMatter { get; }
    internal IReadOnlyList<MdHeading> Headings { get; }
    internal IReadOnlyList<MdSection> Sections { get; }
    internal IReadOnlyList<MdLink> Links { get; }
    internal IReadOnlyList<MdFence> Fences { get; }
    internal IReadOnlyList<MdTextRegion> TextRegions { get; }
    internal IReadOnlyList<MdDiagnostic> Diagnostics { get; }
    internal IReadOnlyList<MdCapability> Capabilities { get; }
    internal MdCoverage Coverage { get; }
}
