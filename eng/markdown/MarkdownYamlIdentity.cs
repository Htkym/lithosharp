using System;
using System.Reflection;
using YamlDotNet.Core;

namespace LithoSharp.Markdown.Hosting;

// Consumer startup adapter, outside canonical parser source. No file access or assembly loading.
internal static class MarkdownYamlIdentity
{
    internal const string PackageVersion = "18.1.0";
    internal const string AssemblyIdentity = "YamlDotNet, Version=18.0.0.0, Culture=neutral, PublicKeyToken=ec19458f3c15af5e";
    internal const string InformationalVersion = "18.1.0";

    internal static void RequirePinned() => Require(PackageVersion, AssemblyIdentity, InformationalVersion);
    internal static void Require(string expectedPackage, string expectedAssembly, string expectedInformation)
    {
        var loaded = typeof(Parser).Assembly;
        var information = loaded.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        // Package and assembly versions have different meanings; do not compare18.1.0 to18.0.0.0.
        if (string.IsNullOrEmpty(expectedPackage) || string.IsNullOrEmpty(expectedAssembly)
            || string.IsNullOrEmpty(expectedInformation) || string.IsNullOrEmpty(loaded.FullName) || string.IsNullOrEmpty(information)
            || !string.Equals(expectedPackage, PackageVersion, StringComparison.Ordinal)
            || !string.Equals(loaded.FullName, expectedAssembly, StringComparison.Ordinal)
            || !string.Equals(information, expectedInformation, StringComparison.Ordinal))
            throw new InvalidOperationException("Loaded Markdown YAML dependency does not match the pinned identity.");
    }
}
