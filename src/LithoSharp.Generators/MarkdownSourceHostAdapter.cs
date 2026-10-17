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
    internal const string ExpectedParserVersion = "1/7e854150d5cad96e554b90a550251dce74d29b79ceef2964fa9cfa4a226e2051";
    private const string ContractVersion = "1.0";
    private const string ProfileId = "lithosharp-markdown/1";
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
#if SYNTAMARK_SOURCE
        if (!string.Equals(MdParserVersion.Value, expectedParserVersion, StringComparison.Ordinal))
            throw new InvalidOperationException("Compiled Markdown source differs from the expected artifact.");
#endif
        MarkdownYamlIdentity.Require(yamlPackage, yamlAssembly, yamlInformation);
    }

#if SYNTAMARK_SOURCE
    internal static MdDocumentFacts ParseForCompiler(string raw, string scopeId, string sourceId, CancellationToken cancellationToken)
        => Parse(raw, scopeId, sourceId, null, new MdOptions(profileId: ProfileId),
            ExpectedParserVersion, ContractVersion, ProfileId, MarkdownYamlIdentity.PackageVersion,
            MarkdownYamlIdentity.AssemblyIdentity, MarkdownYamlIdentity.InformationalVersion, cancellationToken);

    internal static MdDocumentFacts Parse(string raw, string scopeId, string sourceId, string? sourceVersion,
        MdOptions options, string expectedParserVersion, string contractVersion, string profileId,
        string yamlPackage, string yamlAssembly, string yamlInformation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Verify(expectedParserVersion, contractVersion, profileId, yamlPackage, yamlAssembly, yamlInformation);
        if (options.ProfileId != profileId)
            throw new InvalidOperationException("Markdown options differ from the verified profile.");
        return MdParser.Parse(raw, scopeId, sourceId, sourceVersion, options, cancellationToken);
    }
#endif
}
