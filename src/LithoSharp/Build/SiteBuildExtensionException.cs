using LithoSharp.Content;
using LithoSharp.Diagnostics;

namespace LithoSharp.Build;

/// <summary>Structured diagnostics raised during build preparation, before publication.</summary>
public sealed class SiteBuildExtensionException : InvalidOperationException
{
    /// <summary>Creates a failure with at least one error diagnostic.</summary>
    public SiteBuildExtensionException(IEnumerable<SiteDiagnostic> diagnostics)
        : this(ContentDiagnostics.Snapshot(diagnostics)) { }

    private SiteBuildExtensionException(IReadOnlyList<SiteDiagnostic> diagnostics)
        : base(string.Join(Environment.NewLine, diagnostics.Select(diagnostic => $"{diagnostic.Id}: {diagnostic.Message}")))
    {
        if (!diagnostics.Any(diagnostic => diagnostic.Severity == SiteDiagnosticSeverity.Error))
            throw new ArgumentException("A preparation failure requires an error diagnostic.", nameof(diagnostics));
        Diagnostics = diagnostics;
    }

    /// <summary>The source-mapped diagnostics for this failure.</summary>
    public IReadOnlyList<SiteDiagnostic> Diagnostics { get; }
}
