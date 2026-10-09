using System;
using System.Linq;
using LithoSharp.Diagnostics;

namespace LithoSharp.Internal;

// Compiled privately into the analyzer: only pure runtime validation is shared.
internal static class StaticApiGuards
{
    internal static void ValidateAbsoluteUrl(string value)
    {
        if (value is null) throw new ArgumentNullException(nameof(value));
        if (value.Any(char.IsControl) || value.Any(char.IsWhiteSpace) || value.IndexOf('\\') >= 0
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !uri.IsWellFormedOriginalString())
            throw new UriFormatException("The URL must be an absolute, well-formed HTTP or HTTPS URL.");
    }

    internal static void ValidateFailureThreshold(int failureThreshold)
    {
        if (!Enum.IsDefined(typeof(SiteDiagnosticSeverity), failureThreshold))
            throw new ArgumentOutOfRangeException(nameof(failureThreshold));
    }
}
