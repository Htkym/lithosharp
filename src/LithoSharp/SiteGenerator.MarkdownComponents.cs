using LithoSharp.Build;
using LithoSharp.Content;
using LithoSharp.Documentation;
using LithoSharp.Quality;
using System.Text.Json;

namespace LithoSharp;

public sealed partial class SiteGenerator
{
    internal ComponentRenderingContext CreateMarkdownComponentContext(RenderContext configuration, DocumentKey? document = null)
    {
        var matches = document is null ? [] : configuration.DocumentSemantics
            .SelectMany(snapshot => snapshot.Documents.Where(current => current.Key == document)
                .Select(current => (Snapshot: snapshot, Document: current))).Take(2).ToArray();
        return new(this, configuration)
        {
            EnvironmentName = configuration.EnvironmentName,
            Assets = configuration.Assets,
            Document = matches.Length == 1 ? matches[0].Document : null,
            DocumentSemantics = matches.Length == 1 ? matches[0].Snapshot : null,
        };
    }

    internal string RenderMarkdown(string markdown, ComponentRenderingContext context)
    {
        var compiled = _markdownCompiler.Compile(markdown, context);
        var errors = compiled.Semantics?.Diagnostics.Where(static diagnostic => diagnostic.Severity == Diagnostics.SiteDiagnosticSeverity.Error).ToArray() ?? [];
        if (errors.Length != 0)
            throw new SiteQualityValidationException(new SiteQualityReport(errors));
        return compiled.Html;
    }

    private (IReadOnlyList<BuildInput> Inputs, IReadOnlyList<BuildNodeId> Assets) CaptureMarkdownComponentInputs(string outputRoot,
        IReadOnlyList<DocumentSemanticSnapshot> documentSemantics)
    {
        var registry = _markdownCompiler.Options.Markdown.Components;
        var inputs = new List<BuildInput>
        {
            BuildInput.FromConfiguration("markdown.profile", _markdownCompiler.Options.Markdown.ProfileId),
            BuildInput.FromConfiguration("markdown.components", JsonSerializer.Serialize(registry?.Schemas ?? [])),
            BuildInput.FromConfiguration("markdown.componentReferences", registry?.HasReferenceRules == true
                ? JsonSerializer.Serialize(documentSemantics.Select(static snapshot => snapshot.ToJson())) : "[]"),
        };
        var assets = new List<BuildNodeId>();
        if (registry is null)
            return (inputs, assets);
        foreach (var registration in registry.Registrations)
        {
            inputs.Add(BuildInput.FromConfiguration("markdown.component:" + registration.Name, registration.Fingerprint));
            foreach (var dependency in registration.Dependencies)
            {
                var key = "markdown-components/" + registration.Name + "/" + dependency.Key;
                switch (dependency.Kind)
                {
                    case ContentDependencyKind.File:
                        var root = registration.DependencyRoot!;
                        var path = Path.GetFullPath(Path.Combine(root, dependency.PhysicalFilePath));
                        if (ContainsInputDirectory(outputRoot, path))
                            throw new ArgumentException("Markdown component dependencies must be outside the output directory.");
                        inputs.Add(BuildInput.FromFile(key, BuildInputFingerprint.FromContainedFile(root, dependency.PhysicalFilePath)));
                        break;
                    case ContentDependencyKind.Asset:
                        assets.Add(new BuildNodeId("asset:" + dependency.Key));
                        break;
                    default:
                        inputs.Add(BuildInput.FromValue(key, dependency.Value!));
                        break;
                }
            }
        }
        return (inputs, assets);
    }
}
