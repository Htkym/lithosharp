using System;

namespace LithoSharp.Internal;

// Shared pure LSQ001 URL checks. Site membership/anchors and base-dependent resolution
// remain runtime concerns; the compiler projection can prove only independent issues.
internal enum SiteQualityUrlIssue { None, EmptyResource, InvalidReference, UnsupportedAddress }

internal static class SiteQualityUrlRules
{
    internal static SiteQualityUrlIssue Resolve(string value, bool resource, Uri resolutionBase, out Uri address)
    {
        address = null!;
        if (resource && string.IsNullOrWhiteSpace(value)) return SiteQualityUrlIssue.EmptyResource;
        if (!Uri.TryCreate(resolutionBase, value.Trim(), out var resolved)) return SiteQualityUrlIssue.InvalidReference;
        address = resolved;
        return Classify(resolved);
    }

    internal static SiteQualityUrlIssue IndependentIssue(string value, bool resource)
    {
        if (resource && string.IsNullOrWhiteSpace(value)) return SiteQualityUrlIssue.EmptyResource;
        var reference = value.Trim();
        // Rooted/protocol-relative paths may look like file URIs on some hosts,
        // but their HTML meaning requires the actual base.
        if (!HasScheme(reference)) return SiteQualityUrlIssue.None;
        if (!Uri.TryCreate(reference, UriKind.RelativeOrAbsolute, out var address))
            return SiteQualityUrlIssue.InvalidReference;
        return address.IsAbsoluteUri ? Classify(address) : SiteQualityUrlIssue.None;
    }

    private static SiteQualityUrlIssue Classify(Uri address)
    {
        if (address.Scheme is "mailto" or "tel" or "data") return SiteQualityUrlIssue.None;
        return address.Scheme is not ("http" or "https") || address.UserInfo.Length != 0
            ? SiteQualityUrlIssue.UnsupportedAddress : SiteQualityUrlIssue.None;
    }

    private static bool HasScheme(string value)
    {
        if (value.Length == 0 || !Letter(value[0])) return false;
        for (var i = 1; i < value.Length; i++)
        {
            var c = value[i];
            if (c == ':') return true;
            if (!Letter(c) && !(c >= '0' && c <= '9') && c is not ('+' or '-' or '.')) return false;
        }
        return false;
    }
    private static bool Letter(char c) => c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z';
}
