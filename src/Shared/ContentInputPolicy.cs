#nullable enable
using System;
using System.Linq;
using System.Text;

namespace LithoSharp.Content;

/// <summary>Selects the rules shared by compiler declarations and runtime discovery.</summary>
#if NETSTANDARD2_0
internal enum ContentInputProfile
#else
public enum ContentInputProfile
#endif
{
    /// <summary>Markdown documents, excluding underscore-prefixed files and directories.</summary>
    Markdown,
    /// <summary>All bundle files, with reserved Markdown names classified separately.</summary>
    OpenKnowledgeFormat,
}

/// <summary>The role of an input before its content is parsed or validated.</summary>
#if NETSTANDARD2_0
internal enum ContentInputKind
#else
public enum ContentInputKind
#endif
{
    /// <summary>An ordinary Markdown document.</summary>
    Markdown,
    /// <summary>An OKF concept document.</summary>
    Concept,
    /// <summary>An OKF directory index, including the optional bundle root index.</summary>
    Index,
    /// <summary>An OKF update history.</summary>
    Log,
    /// <summary>A bundle file retained without interpreting it as a concept.</summary>
    Auxiliary,
}

internal static class ContentInputPolicy
{
    // Reject case-only collisions on every host so a catalog is portable to Windows.
    internal static StringComparer PathComparer { get; } = StringComparer.OrdinalIgnoreCase;

    internal static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path != path.Trim() || path.Any(char.IsControl))
            throw new ArgumentException("Content input paths must be nonempty and contain no surrounding whitespace or control characters.", nameof(path));
        var normalized = path.Replace('\\', '/').Normalize(NormalizationForm.FormC);
        if (normalized.StartsWith("/", StringComparison.Ordinal) || normalized.IndexOf(':') >= 0
            || normalized.Split('/').Any(segment => segment.Length == 0 || segment is "." or ".."))
            throw new ArgumentException("Content input paths must be relative and must not contain empty or traversal segments.", nameof(path));
        return Routing.SiteRoute.NormalizeRelativeOutputPath(normalized);
    }

    internal static bool IsPartial(string relativePath) =>
        relativePath.Replace('\\', '/').Split('/').Any(segment => segment.StartsWith("_", StringComparison.Ordinal));

    internal static bool TryIdentity(string value, out string identity)
    {
        identity = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Any(char.IsControl))
            return false;
        try
        {
            identity = value.Normalize(NormalizationForm.FormC);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    internal static bool TryScope(string value, out string identity)
    {
        identity = string.Empty;
        return value.Length == 0 || TryIdentity(value, out identity);
    }

    internal static ContentInputKind? Classify(string relativePath, ContentInputProfile profile)
    {
        var path = NormalizePath(relativePath);
        var markdown = path.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
        switch (profile)
        {
            case ContentInputProfile.Markdown:
                return markdown && !IsPartial(path) ? ContentInputKind.Markdown : null;
            case ContentInputProfile.OpenKnowledgeFormat:
                if (!markdown)
                    return ContentInputKind.Auxiliary;
                var name = path.Substring(path.LastIndexOf('/') + 1);
                // OKF v0.2 names these exact reserved filenames at every level.
                return name == "index.md" ? ContentInputKind.Index
                    : name == "log.md" ? ContentInputKind.Log : ContentInputKind.Concept;
            default:
                throw new ArgumentOutOfRangeException(nameof(profile));
        }
    }

    internal static string RelativeToRoot(string sourcePath, string inputRoot)
    {
        var source = NormalizePath(sourcePath);
        if (inputRoot.Length == 0)
            return source;
        var root = NormalizePath(inputRoot);
        if (!source.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Content input '" + source + "' is outside declared input root '" + root + "'.", nameof(sourcePath));
        return source.Substring(root.Length + 1);
    }

    internal static string ProjectRelativePath(string path, string projectDirectory)
    {
        var relative = path.Replace('\\', '/');
        var root = projectDirectory.Replace('\\', '/').TrimEnd('/');
        if (relative.Split('/').Any(segment => segment is "." or "..") || root.Split('/').Any(segment => segment is "." or ".."))
            throw new ArgumentException("Static input paths must not contain traversal segments.");
        if (root.Length != 0)
        {
            if (!relative.StartsWith(root + "/", System.IO.Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new ArgumentException("Static content files must be inside MSBuildProjectDirectory.");
            relative = relative.Substring(root.Length + 1);
        }
        else if (relative.StartsWith("/", StringComparison.Ordinal) || relative.IndexOf(':') >= 0)
            throw new ArgumentException("MSBuildProjectDirectory is required for absolute AdditionalFiles paths.");
        _ = NormalizePath(relative);
        return relative;
    }

    internal static string RelativeToDeclaredRoot(string projectRelativePath, string inputRoot)
    {
        if (inputRoot.Length == 0)
            return NormalizePath(projectRelativePath);
        // Check the actual directory spelling before normalizing the input identity.
        var root = inputRoot.Replace('\\', '/');
        if (!projectRelativePath.StartsWith(root + "/", System.IO.Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Content input '" + projectRelativePath + "' is outside declared input root '" + root + "'.", nameof(projectRelativePath));
        return NormalizePath(projectRelativePath.Substring(root.Length + 1));
    }
}
