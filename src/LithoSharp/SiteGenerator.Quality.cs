using LithoSharp.Build;
using LithoSharp.Diagnostics;
using LithoSharp.Quality;
using LithoSharp.Routing;

namespace LithoSharp;

public sealed partial class SiteGenerator
{
    private sealed record RedirectOutput(SiteRoute Source, SiteRoute Target, SiteTemplateFile File);

    private static IReadOnlyList<RedirectOutput> PrepareRedirects(
        IReadOnlyList<SiteRedirect> redirects, string baseUrl, SiteRouteTable routeTable, IEnumerable<string> artifactPaths)
    {
        var normalized = redirects.Select(redirect => new SiteRedirect(
            redirect.Source.WithBaseUrl(baseUrl), redirect.Target.WithBaseUrl(baseUrl))).ToArray();
        var registered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var redirect in normalized)
        {
            var owner = "redirect:" + redirect.Source.RelativeOutputPath;
            routeTable.Register(redirect.Source, registered.Add(redirect.Source.RelativeOutputPath) ? owner : owner + ":duplicate");
        }
        routeTable.ValidateOrThrow();
        if (normalized.Length == 0) return [];

        var bySource = normalized.ToDictionary(redirect => redirect.Source.RelativeOutputPath, StringComparer.Ordinal);
        var knownPaths = artifactPaths.ToHashSet(StringComparer.Ordinal);
        var processed = new HashSet<string>(StringComparer.Ordinal);
        var cycles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in bySource.Keys)
        {
            var path = new List<string>();
            var positions = new Dictionary<string, int>(StringComparer.Ordinal);
            var current = source;
            while (bySource.TryGetValue(current, out var redirect) && !processed.Contains(current))
            {
                if (positions.TryGetValue(current, out var cycleStart))
                {
                    cycles.UnionWith(path.Skip(cycleStart));
                    break;
                }
                positions.Add(current, path.Count);
                path.Add(current);
                current = redirect.Target.RelativeOutputPath;
            }
            processed.UnionWith(path);
        }
        var diagnostics = new List<SiteDiagnostic>();
        foreach (var redirect in normalized)
        {
            var source = redirect.Source.RelativeOutputPath;
            if (cycles.Contains(source)) Add("LSQ004", "Redirect participates in a cycle.");
            else if (bySource.ContainsKey(redirect.Target.RelativeOutputPath)) Add("LSQ005", "Redirect chains are not supported; target the final route directly.");
            else if (!knownPaths.Contains(redirect.Target.RelativeOutputPath)) Add("LSQ006", $"Redirect target '{redirect.Target.PublicPath}' does not exist.");
            void Add(string id, string message) => diagnostics.Add(new SiteDiagnostic(id, SiteDiagnosticSeverity.Error, message, new SiteSourceLocation(source)));
        }
        if (diagnostics.Count != 0) throw new SiteQualityValidationException(new SiteQualityReport(diagnostics));
        var origin = new Uri(baseUrl).GetLeftPart(UriPartial.Authority);
        return normalized.Select(redirect => new RedirectOutput(redirect.Source, redirect.Target, new SiteTemplateFile
        {
            RelativePath = redirect.Source.RelativeOutputPath,
            Content = $"<!doctype html>\n<html><head><meta charset=\"utf-8\"><title>Redirect</title><meta name=\"robots\" content=\"noindex\"><link rel=\"canonical\" href=\"{Html.Encode(origin + redirect.Target.PublicPath)}\"><meta http-equiv=\"refresh\" content=\"0;url={Html.Encode(redirect.Target.PublicPath)}\"></head><body><a href=\"{Html.Encode(redirect.Target.PublicPath)}\">Continue</a></body></html>\n"
        })).ToArray();
    }

    private static IEnumerable<BuildNode> CreateRedirectNodes(IReadOnlyList<RedirectOutput> redirects, string baseUrl, SiteBuildPlan plan)
    {
        var owners = plan.Artifacts.ToDictionary(artifact => artifact.RelativeOutputPath, artifact => artifact.OwnerNodeId, StringComparer.Ordinal);
        foreach (var redirect in redirects)
        {
            var id = new BuildNodeId("redirect:" + redirect.Source.RelativeOutputPath);
            yield return new BuildNode(id,
                [BuildInput.FromValue("redirect.source", redirect.Source.PublicPath),
                 BuildInput.FromValue("redirect.target", redirect.Target.PublicPath),
                 BuildInput.FromConfiguration("site.baseUrl", baseUrl)],
                [owners[redirect.Target.RelativeOutputPath]],
                [new BuildArtifact(new BuildArtifactId("artifact:" + id.Value), id, redirect.Source.RelativeOutputPath)]);
        }
    }

    private static string EscapeOutputPath(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
}
