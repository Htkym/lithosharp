using System;
using Syntamark.Compilation;

namespace Syntamark.Hosting;

// The sealed pair hash identifies the compiled native YAML implementation too.
internal static class MarkdownYamlIdentity
{
    internal const string ProfileId = "native-yaml/1";
    internal const string ParserVersion = "1/42136e5e42d027dc15b4487b4fe381b21bcaf47ab1d30fb954ec21fbff25560f";

    internal static void RequirePinned() => Require(ProfileId);
    internal static void Require(string expectedProfile)
    {
        if (!string.Equals(expectedProfile, ProfileId, StringComparison.Ordinal)
            || !string.Equals(MdParserVersion.Value, ParserVersion, StringComparison.Ordinal))
            throw new InvalidOperationException("Native Markdown YAML does not match the pinned profile and parser identity.");
    }
}
