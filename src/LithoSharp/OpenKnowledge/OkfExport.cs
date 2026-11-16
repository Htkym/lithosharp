using LithoSharp.Build;
using LithoSharp.Quality;

namespace LithoSharp.OpenKnowledge;

public sealed partial class OkfBundle
{
    /// <summary>Copies the captured bundle without executing code or changing any file bytes.</summary>
    /// <remarks>Requires a complete file snapshot and a new destination outside the input tree. Syntax conformance is independent of preservation.</remarks>
    public async Task ExportPreservedAsync(string outputDirectory, CancellationToken cancellationToken = default)
    {
        if (!SnapshotComplete)
            throw new InvalidOperationException("A partial file snapshot cannot be exported as a preserved bundle.");
        await OkfExport.WriteFilesAsync(Files, InputRoot, outputDirectory, cancellationToken).ConfigureAwait(false);
    }
}

internal static class OkfExport
{
    internal static async Task WriteFilesAsync(IReadOnlyList<OkfFile> files, string? inputRoot, string outputDirectory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
        var parent = Path.GetDirectoryName(output) ?? throw new ArgumentException("Export cannot target a filesystem root.", nameof(outputDirectory));
        if (inputRoot is not null)
        {
            var relative = Path.GetRelativePath(inputRoot, output);
            if (relative == "." || !Path.IsPathRooted(relative) && relative != ".."
                && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new ArgumentException("The export directory must be outside the input bundle.", nameof(outputDirectory));
        }
        GeneratedSiteInspector.EnsureSafeAncestors(output);
        if (Path.Exists(output))
            throw new IOException("Preserved export requires a new directory; existing files and indexes are never overwritten.");
        Directory.CreateDirectory(parent);
        GeneratedSiteInspector.EnsureSafeAncestors(parent);
        var staging = Path.Combine(parent, ".okf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(Path.Combine(staging, file.Path.Replace('/', Path.DirectorySeparatorChar)));
            SiteGenerator.EnsureContainedPathHasNoNameSurrogateReparsePoints(staging, path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            GeneratedSiteInspector.EnsureSafeAncestors(path);
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.Asynchronous);
            BuildInputFingerprint.VerifyOpenedContainedFile(staging, path, stream.SafeFileHandle);
            await stream.WriteAsync(file.Bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            BuildInputFingerprint.VerifyOpenedContainedFile(staging, path, stream.SafeFileHandle);
        }
        cancellationToken.ThrowIfCancellationRequested();
        GeneratedSiteInspector.EnsureSafeAncestors(parent);
        Directory.Move(staging, output);
    }
}
