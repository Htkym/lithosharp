using LithoSharp.Diagnostics;

namespace LithoSharp.Content;

public sealed class ContentEntry<TFrontMatter, TBody>
    where TFrontMatter : notnull
    where TBody : notnull
{
    private IReadOnlyList<ContentDependency> declaredDependencies = [];
    private IReadOnlyList<CapturedContentInput> capturedInputs = [];

    // Only built-in file readers can attach raw-byte provenance. Caller-provided
    // fingerprints and in-memory bodies do not establish a raw-file snapshot.
    internal IReadOnlyList<CapturedContentInput> CapturedInputs
    {
        get => capturedInputs;
        init => capturedInputs = Array.AsReadOnly(value.ToArray());
    }

    /// <summary>Additional dependencies specific to this entry.</summary>
    public IReadOnlyList<ContentDependency> DeclaredDependencies
    {
        get => declaredDependencies;
        init => declaredDependencies = ContentDependency.Snapshot(value ?? throw new ArgumentNullException(nameof(value)));
    }

    /// <summary>Discovery surfaces for this entry. Direct publication is controlled separately by metadata.</summary>
    public GeneratedPageDerivedSurfaces DerivedSurfaces { get; init; } = GeneratedPageDerivedSurfaces.All;

    public ContentEntry(
        ContentEntryId id,
        string sourcePath,
        string sourceFingerprint,
        TFrontMatter frontMatter,
        TBody body,
        SiteSourceLocation? sourceLocation = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(sourceFingerprint);
        ArgumentNullException.ThrowIfNull(frontMatter);
        ArgumentNullException.ThrowIfNull(body);

        Id = id;
        SourcePath = Build.BuildInput.FromFile(sourcePath).Key;
        SourceFingerprint = ContentIdentity.Normalize(sourceFingerprint, nameof(sourceFingerprint));
        FrontMatter = frontMatter;
        Body = body;
        SourceLocation = sourceLocation;
    }

    public ContentEntryId Id { get; }

    /// <summary>Input-root-relative source path with slash separators.</summary>
    public string SourcePath { get; }

    public string SourceFingerprint { get; }

    public TFrontMatter FrontMatter { get; }

    public TBody Body { get; }

    public SiteSourceLocation? SourceLocation { get; }
}

// Hash of the exact verified bytes consumed before any text transformation.
internal sealed record CapturedContentInput(string File, string Hash);
