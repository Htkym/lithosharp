using System;
using Syntamark.Compilation;

namespace Syntamark.Hosting;

// The sealed pair hash identifies the compiled native YAML implementation too.
internal static class MarkdownYamlIdentity
{
    internal const string ProfileId = "native-yaml/1";
    internal const string ParserVersion = "1/1fc1f7dfbbac0b3e12cc085e91210903fbf0e4d75865b7f8feeb82d76073e3e0";

    internal static void RequirePinned() => Require(ProfileId);
    internal static void Require(string expectedProfile)
    {
        if (!string.Equals(expectedProfile, ProfileId, StringComparison.Ordinal)
            || !string.Equals(MdParserVersion.Value, ParserVersion, StringComparison.Ordinal))
            throw new InvalidOperationException("Native Markdown YAML does not match the pinned profile and parser identity.");
    }
}
