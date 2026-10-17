using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace LithoSharp.Internal;

// Compiler-buffer provenance only. No disk/network reads, runtime assembly load or user execution.
internal static class StaticManifestFingerprint
{
    internal const string Contract = "explicit-manifest-route-lookup/1";
    internal const int MaxInputs = 8192;
    internal const int MaxTextUnits = 8 * 1024 * 1024;
    internal static readonly string[] Metadata = { "LithoSharpCollection", "LithoSharpId", "LithoSharpRoute", "LithoSharpSite", "LithoSharpVariant" };
    internal static readonly string[] Properties = { "MSBuildProjectDirectory", "TargetFramework", "Configuration", "LithoSharpAnalysisProfile" };

    internal static bool OwnGenerated(SyntaxTree tree) => tree.FilePath.Replace('\\', '/')
        .Contains("LithoSharp.Generators/LithoSharp.Generators.StaticContentGenerator/");

    internal static string? Create(Compilation compilation, IEnumerable<AdditionalText> files,
        AnalyzerConfigOptionsProvider options, CancellationToken cancellation)
    {
        var inputs = files.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
        var trees = compilation.SyntaxTrees.Where(t => !OwnGenerated(t)).OrderBy(t => t.FilePath, StringComparer.Ordinal).ToArray();
        if (inputs.Length + trees.Length > MaxInputs) return null;
        var text = new StringBuilder();
        var units = 0;
        bool Add(string value)
        {
            cancellation.ThrowIfCancellationRequested();
            if (value.Length > MaxTextUnits - units) return false;
            units += value.Length;
            text.Append(value.Length).Append(':').Append(value);
            return true;
        }
        if (!Add(Contract) || !Add(compilation.AssemblyName ?? "")) return null;
        foreach (var key in Properties)
        {
            options.GlobalOptions.TryGetValue("build_property." + key, out var value);
            if (!Add(key) || !Add(value ?? "")) return null;
        }
        foreach (var tree in trees)
        {
            if (!Add("source") || !Add(tree.FilePath) || !Add(tree.GetText(cancellation).ToString())) return null;
            if (tree.Options is CSharpParseOptions parse)
            {
                if (!Add(parse.LanguageVersion.ToString()) || !Add(parse.Kind.ToString())) return null;
                if (!Add(parse.PreprocessorSymbolNames.Count().ToString(System.Globalization.CultureInfo.InvariantCulture))) return null;
                foreach (var symbol in parse.PreprocessorSymbolNames.OrderBy(x => x, StringComparer.Ordinal)) if (!Add(symbol)) return null;
            }
        }
        foreach (var file in inputs)
        {
            var source = file.GetText(cancellation);
            if (source is null || !Add("additional") || !Add(file.Path) || !Add(source.ToString())) return null;
            foreach (var key in Metadata)
            {
                options.GetOptions(file).TryGetValue("build_metadata.AdditionalFiles." + key, out var value);
                if (!Add(key) || !Add(value ?? "")) return null;
            }
        }
        foreach (var reference in compilation.References.OrderBy(r => r.Display, StringComparer.Ordinal))
        {
            if (reference is not PortableExecutableReference pe || pe.GetMetadata() is not AssemblyMetadata assembly) return null;
            if (!Add("reference") || !Add(reference.Display ?? "") || !Add(compilation.GetAssemblyOrModuleSymbol(reference)?.ToDisplayString() ?? "")) return null;
            if (!Add(assembly.GetModules().Length.ToString(System.Globalization.CultureInfo.InvariantCulture))) return null;
            foreach (var module in assembly.GetModules()) if (!Add(module.GetModuleVersionId().ToString("D"))) return null;
            if (!Add(reference.Properties.Aliases.Length.ToString(System.Globalization.CultureInfo.InvariantCulture))) return null;
            foreach (var alias in reference.Properties.Aliases.OrderBy(x => x, StringComparer.Ordinal)) if (!Add(alias)) return null;
            if (!Add(reference.Properties.EmbedInteropTypes.ToString())) return null;
        }
        using var hash = SHA256.Create();
        try { return BitConverter.ToString(hash.ComputeHash(new UnicodeEncoding(false, false, true).GetBytes(text.ToString()))).Replace("-", "").ToLowerInvariant(); }
        catch (EncoderFallbackException) { return null; }
    }
}
