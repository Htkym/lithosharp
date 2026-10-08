using System;
using LithoSharp.Markdown.Hosting;
#if LITHOSHARP_MARKDOWN_SOURCE
using System.Threading;
using LithoSharp.Content.Compilation;
#endif

namespace LithoSharp.Generators;

// Prepared consumer adapter. MD-05's exact source package defines LITHOSHARP_MARKDOWN_SOURCE.
// No current checkout parser Compile links or public runtime facade references are used.
internal static class MarkdownSourceHostAdapter
{
    internal static void Verify(string expectedParserVersion, string contractVersion, string profileId,
        string yamlPackage, string yamlAssembly, string yamlInformation)
    {
        if (string.IsNullOrEmpty(expectedParserVersion) || expectedParserVersion.Length != 66
            || !expectedParserVersion.StartsWith("1/", StringComparison.Ordinal)
            || contractVersion != "1.0" || profileId != "lithosharp-markdown/1")
            throw new InvalidOperationException("Markdown source metadata does not match the supported contract.");
        for (var i = 2; i < expectedParserVersion.Length; i++)
            if (!(expectedParserVersion[i] is >= '0' and <= '9' or >= 'a' and <= 'f'))
                throw new InvalidOperationException("Markdown parser hash must be lowercase hex64.");
#if LITHOSHARP_MARKDOWN_SOURCE
        if (!string.Equals(MdParserVersion.Value, expectedParserVersion, StringComparison.Ordinal))
            throw new InvalidOperationException("Compiled Markdown source differs from the expected artifact.");
#endif
        MarkdownYamlIdentity.Require(yamlPackage, yamlAssembly, yamlInformation);
    }

#if LITHOSHARP_MARKDOWN_SOURCE
    internal static MdDocumentFacts Parse(string raw, string scopeId, string sourceId, string? sourceVersion,
        MdOptions options, string expectedParserVersion, string contractVersion, string profileId,
        string yamlPackage, string yamlAssembly, string yamlInformation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Verify(expectedParserVersion, contractVersion, profileId, yamlPackage, yamlAssembly, yamlInformation);
        return MdParser.Parse(raw, scopeId, sourceId, sourceVersion, options, cancellationToken);
    }
#endif
}
