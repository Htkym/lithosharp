using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using LithoSharp.Diagnostics;

namespace LithoSharp.Quality;

// Explicit post-publication inspection only. Generation never calls this class.
internal static class SitePublicationHttpInspector
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static async Task<SiteQualityReport> InspectAsync(SitePublicationManifest manifest,
        CancellationToken cancellationToken, HttpMessageHandler? handler = null, TimeSpan? minimumRequestInterval = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = new List<SiteDiagnostic>();
        var root = new Uri(manifest.BaseUrl, UriKind.Absolute);
        var origin = root.GetLeftPart(UriPartial.Authority);
        var byPath = manifest.Entries.ToDictionary(entry => entry.OutputPath!, StringComparer.Ordinal);
        var interval = minimumRequestInterval ?? TimeSpan.FromMilliseconds(500);
        SocketsHttpHandler? transport = null;
        if (handler is null)
        {
            transport = ExternalLinkChecker.CreateClient();
            transport.AutomaticDecompression = DecompressionMethods.All;
        }
        using var client = new HttpClient(handler ?? transport!, disposeHandler: handler is null)
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        var inspected = 0;
        foreach (var entry in manifest.Entries.Where(entry => ExpectedMime(entry.OutputPath!) is not null))
        {
            var allowedUrls = entry.Urls!.Select(url => new Uri(url, UriKind.Absolute).AbsoluteUri).ToHashSet(StringComparer.Ordinal);
            if (entry.RedirectTarget is { } target && byPath.TryGetValue(target, out var redirected))
                allowedUrls.UnionWith(redirected.Urls!.Select(url => new Uri(url, UriKind.Absolute).AbsoluteUri));
            foreach (var url in entry.Urls!)
            {
                await Inspect(url, entry.OutputPath!, async (response, token) =>
                {
                    if (response.StatusCode != HttpStatusCode.OK && !(entry.OutputPath == "404.html" && response.StatusCode == HttpStatusCode.NotFound))
                        throw new InvalidDataException($"Expected HTTP 200 (or 404 for the explicit 404.html artifact), observed {(int)response.StatusCode}.");
                    var expected = ExpectedMime(entry.OutputPath!)!;
                    var contentType = response.Content.Headers.ContentType;
                    if (!string.Equals(contentType?.MediaType, expected, StringComparison.OrdinalIgnoreCase)
                        && !(expected == "application/xml" && string.Equals(contentType?.MediaType, "text/xml", StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException($"Expected Content-Type {expected}, observed {contentType?.ToString() ?? "missing"}.");
                    if (expected == "text/markdown")
                    {
                        if (!string.Equals(contentType?.CharSet?.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Markdown must be served with charset=utf-8.");
                        var body = await ReadBoundedAsync(response, 16 * 1024 * 1024, token).ConfigureAwait(false);
                        _ = StrictUtf8.GetString(body);
                        if (Convert.ToHexStringLower(SHA256.HashData(body)) != entry.Sha256)
                            throw new InvalidDataException("The HTTP Markdown body does not match the inspected output artifact.");
                    }
                }, allowedUrls, "LSPHTTP001").ConfigureAwait(false);
                inspected++;
            }
        }

        var basePath = Routing.SiteRoute.ForDirectoryIndex(string.Empty, manifest.BaseUrl).PublicPath;
        var missingUrl = origin + basePath + "__lithosharp-missing-" + Guid.NewGuid().ToString("N") + ".html";
        await Inspect(missingUrl, "(unknown URL)", (response, _) =>
        {
            if (response.StatusCode != HttpStatusCode.NotFound)
                throw new InvalidDataException($"An unknown public URL must return HTTP 404, observed {(int)response.StatusCode}.");
            return Task.CompletedTask;
        }, new HashSet<string>(StringComparer.Ordinal) { new Uri(missingUrl, UriKind.Absolute).AbsoluteUri }, "LSPHTTP003").ConfigureAwait(false);

        var robotsUrl = origin + "/robots.txt";
        byPath.TryGetValue("robots.txt", out var robotsEntry);
        var ownsRootRobots = basePath == "/" && robotsEntry is not null;
        await Inspect(robotsUrl, "(origin root)/robots.txt", async (response, token) =>
        {
            if (!ownsRootRobots && response.StatusCode == HttpStatusCode.NotFound)
            {
                diagnostics.Add(new("LSPHTTP004", SiteDiagnosticSeverity.Info,
                    "Origin-root robots.txt returned HTTP 404. The selected output does not manage that origin-root resource.", new("robots.txt"), "PublishedHttp", null));
                return;
            }
            if (response.StatusCode != HttpStatusCode.OK || !string.Equals(response.Content.Headers.ContentType?.MediaType, "text/plain", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Origin-root robots.txt must return HTTP 200 and text/plain when provided.");
            var body = await ReadBoundedAsync(response, 512 * 1024, token).ConfigureAwait(false);
            _ = StrictUtf8.GetString(body);
            if (ownsRootRobots && Convert.ToHexStringLower(SHA256.HashData(body)) != robotsEntry!.Sha256)
                throw new InvalidDataException("Origin-root robots.txt does not match the selected root site's inspected artifact.");
            if (!ownsRootRobots)
                diagnostics.Add(new("LSPHTTP004", SiteDiagnosticSeverity.Info,
                    "Origin-root robots.txt was observed separately. Its management is external to the selected output, and no generated-robots claim was made.", new("robots.txt"), "PublishedHttp", null));
        }, null, "LSPHTTP004").ConfigureAwait(false);
        diagnostics.Add(new("LSPHTTP000", SiteDiagnosticSeverity.Info,
            $"Explicit public HTTP inspection completed for {inspected} artifact URLs, an unknown-URL probe and origin-root robots.txt. This is separate from generated-output and publication-compatibility checks.", null, "PublishedHttp", null));
        return new(diagnostics);

        async Task Inspect(string url, string path, Func<HttpResponseMessage, CancellationToken, Task> validate,
            IReadOnlySet<string>? allowedUrls, string failureId)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                var current = new Uri(url, UriKind.Absolute);
                var visited = new HashSet<string>(StringComparer.Ordinal);
                for (var hop = 0; hop <= 10; hop++)
                {
                    ExternalLinkChecker.ValidateUri(current);
                    if (!string.Equals(current.GetLeftPart(UriPartial.Authority), origin, StringComparison.OrdinalIgnoreCase)
                        || allowedUrls is not null && !allowedUrls.Contains(current.AbsoluteUri))
                        throw new InvalidDataException("An HTTP redirect left the selected origin or the artifact's declared public routes.");
                    if (!visited.Add(current.AbsoluteUri))
                        throw new InvalidDataException("An HTTP redirect loop was detected.");
                    using var request = new HttpRequestMessage(HttpMethod.Get, current);
                    using var response = await ExternalLinkChecker.SendRateLimitedAsync(client, request, interval, timeout.Token).ConfigureAwait(false);
                    if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                    {
                        var next = new Uri(current, location);
                        ExternalLinkChecker.ValidateUri(next);
                        diagnostics.Add(new("LSPHTTP002", SiteDiagnosticSeverity.Info,
                            $"Observed HTTP {(int)response.StatusCode} redirect from '{current.GetLeftPart(UriPartial.Path)}' to '{next.GetLeftPart(UriPartial.Path)}'.", new(path), "PublishedHttp", null));
                        current = next;
                        continue;
                    }
                    await validate(response, timeout.Token).ConfigureAwait(false);
                    return;
                }
                throw new InvalidDataException("The HTTP redirect limit was exceeded.");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Failure("The HTTP inspection timed out.");
            }
            catch (Exception exception) when (exception is HttpRequestException or SocketException or InvalidDataException or IOException or InvalidOperationException or UriFormatException or DecoderFallbackException)
            {
                Failure(exception.Message);
            }
            void Failure(string message) => diagnostics.Add(new(failureId, SiteDiagnosticSeverity.Error,
                $"Public HTTP check failed for '{url}': {message}", new(path), "PublishedHttp", null));
        }
    }

    private static string? ExpectedMime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html",
        ".md" => "text/markdown",
        ".txt" when path == "llms.txt" => "text/plain",
        ".xml" when path == "sitemap.xml" => "application/xml",
        _ => null,
    };

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int limit, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > limit)
            throw new InvalidDataException("The HTTP body exceeded the inspection size limit.");
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var count = await input.ReadAsync(buffer, token).ConfigureAwait(false);
            if (count == 0) return output.ToArray();
            if (output.Length + count > limit)
                throw new InvalidDataException("The HTTP body exceeded the inspection size limit.");
            output.Write(buffer, 0, count);
        }
    }
}
