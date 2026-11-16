using System.Text.Json;
using System.Text;
using LithoSharp.Build;
using LithoSharp.Diagnostics;
using LithoSharp.Internal;
using LithoSharp.Routing;

namespace LithoSharp.Quality;

public enum SitePublicationCompatibilityState
{
    NotCompared,
    Compatible,
    Incompatible,
    ExplicitlyAllowed,
}

/// <summary>Compares a candidate with an explicitly selected published baseline. Never writes either artifact.</summary>
public static class SitePublicationCompatibility
{
    /// <summary>Checks published URLs and anchors; allowance keys are exact absolute URLs with optional escaped fragments, and values are nonempty reasons.</summary>
    public static async Task<SiteQualityReport> CompareAsync(SitePublicationManifest current, string? baselineFile,
        IReadOnlyDictionary<string, string> allowedRemovals, bool required, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(allowedRemovals);
        cancellationToken.ThrowIfCancellationRequested();
        SitePublicationManifest baseline;
        try
        {
            if (string.IsNullOrWhiteSpace(baselineFile))
                return NotCompared("No published baseline was selected.", required);
            baseline = SitePublicationManifest.Parse(await ReadTextAsync(baselineFile, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
            or ArgumentException or InvalidOperationException or NotSupportedException or System.Security.SecurityException
            or System.ComponentModel.Win32Exception)
        {
            return NotCompared($"The published baseline could not be read or verified: {exception.Message}", required);
        }
        if (baseline.SiteId != current.SiteId || baseline.BaseUrl != current.BaseUrl || baseline.Scope != current.Scope
            || !baseline.ExcludedOutputPaths.SequenceEqual(current.ExcludedOutputPaths, StringComparer.Ordinal))
            return NotCompared($"Publication identity mismatch (site '{baseline.SiteId}' / '{current.SiteId}', base URL '{baseline.BaseUrl}' / '{current.BaseUrl}'); scope and exact exclusions must also match. Select or migrate the published baseline explicitly.", required);

        var diagnostics = new List<SiteDiagnostic>();
        var currentEntries = current.Entries.ToDictionary(entry => entry.OutputPath!, StringComparer.Ordinal);
        var origin = new Uri(current.BaseUrl).GetLeftPart(UriPartial.Authority);
        var root = SiteRoute.ForDirectoryIndex(string.Empty, current.BaseUrl).PublicPath;
        var publishedAddresses = baseline.Entries.SelectMany(entry => entry.Urls!.SelectMany(url =>
            new[] { url }.Concat(entry.Anchors!.Select(anchor => url + "#" + Uri.EscapeDataString(anchor))))).ToHashSet(StringComparer.Ordinal);
        foreach (var (address, reason) in allowedRemovals)
            if (!publishedAddresses.Contains(address) || string.IsNullOrWhiteSpace(reason))
                diagnostics.Add(new("LSPUB004", SiteDiagnosticSeverity.Error,
                    $"Removal allowance '{address}' must name an exact published URL or anchor and include a reason."));
        var allowed = false;
        foreach (var previous in baseline.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var url in previous.Urls!)
            {
                // Resolve through the same route rules used by output Quality, including index aliases.
                SitePublicationManifest.EntryData? target = null;
                var found = SiteQualityUrlRules.TryInternalOutputPath(new Uri(url), origin, root, out var path)
                    && currentEntries.TryGetValue(path, out target);
                var refresh = found && target!.RedirectTarget is not null;
                if (refresh && target!.RedirectTarget is { } redirect)
                    found = currentEntries.TryGetValue(redirect, out target) && target.RedirectTarget is null;
                if (!found)
                    Missing("LSPUB002", url, "Published URL disappeared");
                foreach (var anchor in previous.Anchors!)
                    if (!found || refresh || !target!.Anchors!.Contains(anchor, StringComparer.Ordinal))
                        Missing("LSPUB003", url + "#" + Uri.EscapeDataString(anchor),
                            refresh ? "Native meta-refresh redirects do not preserve fragments; retain the old anchor at its published URL or explicitly allow its removal"
                                : "Published anchor disappeared; retain its old HTML anchor alias or explicitly allow its removal");
            }
        }
        var state = diagnostics.Any(diagnostic => diagnostic.Severity >= SiteDiagnosticSeverity.Error)
            ? SitePublicationCompatibilityState.Incompatible
            : allowed ? SitePublicationCompatibilityState.ExplicitlyAllowed : SitePublicationCompatibilityState.Compatible;
        return new(diagnostics) { PublicationCompatibility = state, PublicationCompatibilityRequired = required };

        void Missing(string id, string address, string message)
        {
            if (allowedRemovals.TryGetValue(address, out var reason) && !string.IsNullOrWhiteSpace(reason))
            {
                allowed = true;
                diagnostics.Add(new("LSPUB004", SiteDiagnosticSeverity.Info, $"Explicitly allowed removal '{address}': {reason}"));
            }
            else
                diagnostics.Add(new(id, SiteDiagnosticSeverity.Error, $"{message}: '{address}'."));
        }
    }

    internal static SiteQualityReport NotCompared(string message, bool required) =>
        new([new("LSPUB001", required ? SiteDiagnosticSeverity.Error : SiteDiagnosticSeverity.Info, message)])
        { PublicationCompatibility = SitePublicationCompatibilityState.NotCompared, PublicationCompatibilityRequired = required };

    // Both published records and removal allowances must be regular files, not blocking devices or redirected paths.
    internal static async Task<string> ReadTextAsync(string path, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        token.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(fullPath) ?? throw new ArgumentException("A publication record must name a file.", nameof(path));
        GeneratedSiteInspector.EnsureSafeAncestors(parent);
        var attributes = File.GetAttributes(fullPath);
        if ((attributes & (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidOperationException("A publication record must be a regular file without links or reparse points.");
        GeneratedSiteInspector.EnsureEntryType(fullPath, directory: false);
        await using var stream = BuildInputFingerprint.OpenVerifiedContainedRead(parent, fullPath, asynchronous: true);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(token).ConfigureAwait(false);
    }
}
