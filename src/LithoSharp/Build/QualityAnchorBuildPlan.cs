using System.Buffers.Binary;
using System.Security.Cryptography;
using LithoSharp.Quality;

namespace LithoSharp.Build;

// Post-render Quality observations, never render/cache execution nodes.
internal static class QualityAnchorBuildPlan
{
    private const string Marker = "quality.anchor-observation/1";
    internal static BuildNodeId Anchors(string path) => new("quality:anchors:" + Uri.EscapeDataString(path));
    internal static BuildNodeId References(string path) => new("quality:anchor-references:" + Uri.EscapeDataString(path));
    internal static bool IsObservation(BuildNode node) => node.Artifacts.Count == 0
        && node.Inputs.Any(input => input.Kind == BuildInputKind.Value && input.Key == Marker && input.Value == "1");

    internal static SiteBuildPlan Attach(SiteBuildPlan plan, SiteQualityReport report, CancellationToken token)
    {
        var nodes = plan.Nodes.Where(node => !IsObservation(node)).ToList();
        var referencesBySource = report.AnchorReferences.ToLookup(reference => reference.SourcePath, StringComparer.Ordinal);
        foreach (var page in report.HtmlReferences)
        {
            token.ThrowIfCancellationRequested();
            nodes.Add(new(Anchors(page.Identity.OutputPath),
                [BuildInput.FromValue(Marker, "1"), BuildInput.FromValue("route.publicPath", page.Identity.PublicPath),
                 BuildInput.FromValue("anchors.decoded-set", Fingerprint(page.Anchors.Select(anchor => anchor.Decoded), token))]));
        }
        foreach (var path in report.HtmlReferences.Select(page => page.Identity.OutputPath)
            .Concat(report.CssReferences.Select(css => css.OutputPath)).Distinct(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var references = referencesBySource[path].ToArray();
            nodes.Add(new(References(path),
                [BuildInput.FromValue(Marker, "1"), BuildInput.FromValue("anchors.bound-references",
                    Fingerprint(references.Select(reference => reference.TargetPath.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + ":" + reference.TargetPath + reference.Fragment), token))],
                references.Select(reference => Anchors(reference.TargetPath))));
        }
        token.ThrowIfCancellationRequested();
        return SiteBuildPlan.Create(nodes);
    }

    private static string Fingerprint(IEnumerable<string> values, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> buffer = stackalloc byte[512];
        foreach (var value in values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            BinaryPrimitives.WriteInt32LittleEndian(buffer, value.Length);
            hash.AppendData(buffer[..4]);
            for (var start = 0; start < value.Length; start += buffer.Length / 2)
            {
                token.ThrowIfCancellationRequested();
                var count = Math.Min(value.Length - start, buffer.Length / 2);
                for (var i = 0; i < count; i++) BinaryPrimitives.WriteUInt16LittleEndian(buffer[(2 * i)..], value[start + i]);
                hash.AppendData(buffer[..(count * 2)]);
            }
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
