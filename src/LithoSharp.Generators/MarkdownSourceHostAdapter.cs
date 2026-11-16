using System;
using Syntamark.Hosting;
#if SYNTAMARK_SOURCE
using System.Threading;
using Syntamark.Compilation;
#endif

namespace LithoSharp.Generators;

// SYNTAMARK_SOURCE comes from the pinned source package, not checkout Compile links.
internal static class MarkdownSourceHostAdapter
{
    internal const string ExpectedParserVersion = MarkdownYamlIdentity.ParserVersion;
    private const string ContractVersion = "2.0";
    private const string ProfileId = "lithosharp-markdown/1";
    internal static void Verify(string expectedParserVersion, string contractVersion, string profileId,
        string yamlProfileId)
    {
        if (string.IsNullOrEmpty(expectedParserVersion) || expectedParserVersion.Length != 66
            || !expectedParserVersion.StartsWith("1/", StringComparison.Ordinal)
            || contractVersion != ContractVersion || profileId != ProfileId)
            throw new InvalidOperationException("Markdown source metadata does not match the supported contract.");
        for (var i = 2; i < expectedParserVersion.Length; i++)
            if (!(expectedParserVersion[i] is >= '0' and <= '9' or >= 'a' and <= 'f'))
                throw new InvalidOperationException("Markdown parser hash must be lowercase hex64.");
#if SYNTAMARK_SOURCE
        if (!string.Equals(MdParserVersion.Value, expectedParserVersion, StringComparison.Ordinal))
            throw new InvalidOperationException("Compiled Markdown source differs from the expected artifact.");
#endif
        MarkdownYamlIdentity.Require(yamlProfileId);
    }

#if SYNTAMARK_SOURCE
    internal static MdFrontMatterScanResult ParseForCompiler(string raw, CancellationToken cancellationToken)
        => AnalyzeForCompiler(raw, new MdOptions(profileId: ProfileId),
            ExpectedParserVersion, ContractVersion, ProfileId, MarkdownYamlIdentity.ProfileId, cancellationToken);

    internal static MdFrontMatterScanResult AnalyzeForCompiler(string raw, MdOptions options,
        string expectedParserVersion, string contractVersion, string profileId,
        string yamlProfileId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Verify(expectedParserVersion, contractVersion, profileId, yamlProfileId);
        if (options.ProfileId != profileId || options.FrontMatterRecovery != MdFrontMatterRecovery.None
            || options.InputKind != MdInputKind.Document)
            throw new InvalidOperationException("Markdown compiler options differ from the verified strict profile.");
        return MdParser.ScanFrontMatter(raw, options, cancellationToken);
    }

    internal static MdDocumentFacts Parse(string raw, string scopeId, string sourceId, string? sourceVersion,
        MdOptions options, string expectedParserVersion, string contractVersion, string profileId,
        string yamlProfileId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Verify(expectedParserVersion, contractVersion, profileId, yamlProfileId);
        if (options.ProfileId != profileId)
            throw new InvalidOperationException("Markdown options differ from the verified profile.");
        return MdParser.Parse(raw, scopeId, sourceId, sourceVersion, options, cancellationToken);
    }
#endif
}
