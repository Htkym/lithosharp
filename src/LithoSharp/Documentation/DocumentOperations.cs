using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LithoSharp.Content;
using LithoSharp.Diagnostics;

namespace LithoSharp.Documentation;

/// <summary>Explicit document snapshot operations. Existing versions are never overwritten.</summary>
public static class DocumentSnapshot
{
    /// <summary>Copies documents, relative components, code, images and sidebar data as one new version.</summary>
    /// <remarks>Relative references must stay inside the snapshot. @site imports remain shared project inputs.</remarks>
    public static async Task CreateAsync(string sourceDirectory, string destinationDirectory, string version, CancellationToken cancellationToken = default)
    {
        var source = Path.GetFullPath(sourceDirectory);
        var destination = Path.GetFullPath(destinationDirectory);
        _ = new ContentEntryId(version);
        if (Within(source, destination) || Within(destination, source)) throw new ArgumentException("Snapshot inputs and destination must not overlap.");
        if (Path.Exists(destination)) throw new IOException("The version destination already exists; snapshots never overwrite it.");
        CheckParents(destination);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(source, file).Replace('\\', '/');
            if (relative.Split('/').Any(segment => segment is "node_modules" or ".git" or "bin" or "obj")) continue;
            var bytes = await ContentPath.ReadAllBytesAsync(source, file, cancellationToken).ConfigureAwait(false);
            if (Path.GetExtension(file) is ".mdx" or ".md" or ".jsx" or ".tsx" or ".js" or ".ts" or ".css")
            {
                var text = new UTF8Encoding(false, true).GetString(bytes);
                foreach (Match match in Regex.Matches(text, "(?:from\\s*|import\\s*(?:\\(\\s*)?|source=|src=)[\"'](?<path>\\.[^\"']+)[\"']"))
                {
                    var target = Path.GetFullPath(match.Groups["path"].Value.Split('?', '#')[0], Path.GetDirectoryName(file)!);
                    if (!Within(source, target)) throw new IOException($"Snapshot reference escapes its inputs: {relative}: {match.Groups["path"].Value}. Use an explicit @site shared input or include the dependency.");
                    if (!File.Exists(target) && !Directory.Exists(target) && !new[] { ".js", ".jsx", ".ts", ".tsx", ".mdx" }.Any(extension => File.Exists(target + extension)))
                        throw new IOException($"Snapshot contains a missing reference: {relative}: {match.Groups["path"].Value}.");
                }
            }
            files.Add(relative, bytes);
        }
        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, ".snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var (relative, bytes) in files)
            {
                var path = Path.Combine(staging, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
            }
            await File.WriteAllBytesAsync(Path.Combine(staging, "_version.json"), JsonSerializer.SerializeToUtf8Bytes(new
            { version, inputs = files.ToDictionary(pair => pair.Key, pair => Convert.ToHexStringLower(SHA256.HashData(pair.Value))) }), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            CheckParents(destination);
            Directory.Move(staging, destination);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                CheckParents(staging);
                if (Directory.EnumerateFileSystemEntries(staging, "*", SearchOption.AllDirectories).Any(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
                    throw new IOException("Snapshot staging contains a symbolic path and was preserved.");
                Directory.Delete(staging, recursive: true);
            }
        }
    }
    private static bool Within(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
    private static void CheckParents(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Snapshot paths must not traverse symbolic directories.");
    }
}

/// <summary>The explicit behavior for a missing translated message.</summary>
public enum MissingTranslationPolicy
{
    /// <summary>Use the declared source language.</summary>
    Source,
    /// <summary>Return no message.</summary>
    Exclude,
    /// <summary>Fail rather than emit untranslated content.</summary>
    Error
}

/// <summary>A shared, immutable C# and React message catalog.</summary>
public sealed class TranslationCatalog
{
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> messages;
    /// <summary>Snapshots validated language catalogs.</summary>
    public TranslationCatalog(string sourceLocale, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> messages)
    {
        _ = System.Globalization.CultureInfo.GetCultureInfo(sourceLocale);
        ArgumentNullException.ThrowIfNull(messages);
        if (!messages.ContainsKey(sourceLocale)) throw new ArgumentException("The source language catalog is required.");
        SourceLocale = sourceLocale;
        this.messages = messages.ToDictionary(pair => pair.Key, pair => (IReadOnlyDictionary<string, string>)pair.Value.ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var pair in this.messages)
        {
            _ = System.Globalization.CultureInfo.GetCultureInfo(pair.Key);
            foreach (var value in pair.Value) { ArgumentException.ThrowIfNullOrWhiteSpace(value.Key); ArgumentNullException.ThrowIfNull(value.Value); }
        }
    }
    /// <summary>The language used by the explicit source fallback.</summary>
    public string SourceLocale { get; }
    /// <summary>Resolves a message using an explicit missing-translation policy.</summary>
    public string? Get(string locale, string key, MissingTranslationPolicy policy = MissingTranslationPolicy.Error)
    {
        if (messages.TryGetValue(locale, out var selected) && selected.TryGetValue(key, out var translated)) return translated;
        return policy switch
        {
            MissingTranslationPolicy.Exclude => null,
            MissingTranslationPolicy.Source when messages[SourceLocale].TryGetValue(key, out var source) => source,
            _ => throw new KeyNotFoundException($"Missing translation '{key}' for '{locale}'.")
        };
    }
    /// <summary>Returns only the declared message keys for safe browser publication.</summary>
    public IReadOnlyDictionary<string, string> Select(string locale, IEnumerable<string> keys, MissingTranslationPolicy policy = MissingTranslationPolicy.Error) =>
        keys.Distinct(StringComparer.Ordinal).Select(key => (Key: key, Value: Get(locale, key, policy))).Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.Ordinal);
    /// <summary>Reports missing and unused keys against a declared static and dynamic key set.</summary>
    public IReadOnlyList<SiteDiagnostic> Diagnose(IEnumerable<string> usedKeys)
    {
        var used = usedKeys.ToHashSet(StringComparer.Ordinal);
        return messages.SelectMany(locale => used.Except(locale.Value.Keys).Select(key => new SiteDiagnostic("LSDOC201", SiteDiagnosticSeverity.Warning, $"Missing translation '{key}' in '{locale.Key}'."))
            .Concat(locale.Value.Keys.Except(used).Select(key => new SiteDiagnostic("LSDOC202", SiteDiagnosticSeverity.Info, $"Unused translation '{key}' in '{locale.Key}'.")))).ToArray();
    }
    /// <summary>Extracts literal translation calls. Dynamic keys must be registered explicitly.</summary>
    public static IReadOnlyList<string> ExtractKeys(string source) => Regex.Matches(source,
        "(?:\\b(?:t|translate|Translate)\\s*\\(\\s*|<Translate\\s+id=)[\"'](?<key>[^\"']+)[\"']")
        .Select(match => match.Groups["key"].Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
}
