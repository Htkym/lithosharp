using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using LithoSharp.Diagnostics;

namespace LithoSharp.Quality;

internal static class ExternalLinkChecker
{
    private const int CacheVersion = 1;
    private const int MaxRedirects = 10;
    private static readonly SemaphoreSlim RateGate = new(1, 1);
    private static long LastRequest;

    internal static async Task<IReadOnlyList<SiteDiagnostic>> CheckAsync(
        IReadOnlyDictionary<string, SiteSourceLocation> links,
        ExternalLinkCheckOptions options,
        CancellationToken token,
        HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(options);
        var cache = ReadCache(options.CacheFilePath);
        var results = new List<SiteDiagnostic>();
        using var client = handler is null ? new HttpClient(CreateClient(), disposeHandler: true) : new HttpClient(handler, disposeHandler: false);
        client.Timeout = options.RequestTimeout;
        foreach (var pair in links.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            if (!Uri.TryCreate(pair.Key, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || !string.IsNullOrEmpty(uri.Fragment)
                || !string.IsNullOrEmpty(uri.UserInfo))
            {
                results.Add(Failure(pair.Key, pair.Value, "The external link URL is invalid."));
                continue;
            }
            var key = uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);
            if (cache.TryGetValue(key, out var cached) && cached is not null && IsValidCacheEntry(cached) && DateTimeOffset.UtcNow - cached.CheckedAt <= options.CacheDuration)
            {
                if (cached.Status is < 200 or >= 300) results.Add(Failure(key, pair.Value, cached.Message));
                continue;
            }
            try
            {
                using var response = await SendAsync(client, uri, options.MinimumRequestInterval, token).ConfigureAwait(false);
                var message = response.IsSuccessStatusCode ? "" : $"External link returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).";
                cache[key] = new CacheEntry(DateTimeOffset.UtcNow, (int)response.StatusCode, message);
                if (!response.IsSuccessStatusCode) results.Add(Failure(key, pair.Value, message));
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                results.Add(Failure(key, pair.Value, "The external link request timed out."));
            }
            catch (Exception error) when (error is HttpRequestException or SocketException or IOException or InvalidOperationException or UriFormatException)
            {
                results.Add(Failure(key, pair.Value, "The external link request failed: " + error.Message));
            }
        }
        WriteCache(options.CacheFilePath, cache);
        return results;
    }

    internal static bool IsPublicAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return !(bytes[0] is 0 or 10 or 127
                || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
                || (bytes[0] == 169 && bytes[1] == 254)
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 192 && bytes[1] == 0 && bytes[2] is 0 or 2)
                || (bytes[0] == 198 && bytes[1] is 18 or 19)
                || (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100)
                || (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113)
                || bytes[0] >= 224);
        // Restrict IPv6 to global unicast; do not follow transition or documentation addresses.
        return address.AddressFamily == AddressFamily.InterNetworkV6 && (bytes[0] & 0xe0) == 0x20
            && !(bytes[0] == 0x20 && bytes[1] == 0x02)
            && !(bytes[0] == 0x20 && bytes[1] == 0x01 && (bytes[2] < 2 || (bytes[2] == 0x0d && bytes[3] == 0xb8)))
            && !(bytes[0] == 0x3f && bytes[1] == 0xff && (bytes[2] & 0xf0) == 0);
    }

    private static SocketsHttpHandler CreateClient()
    {
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            ConnectCallback = ConnectAsync,
        };
    }

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token).ConfigureAwait(false);
        if (addresses.Length == 0 || addresses.Any(static address => !IsPublicAddress(address))) throw new HttpRequestException("The host resolved to a non-public address.");
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try { await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token).ConfigureAwait(false); return new NetworkStream(socket, ownsSocket: true); }
            catch { socket.Dispose(); if (token.IsCancellationRequested) token.ThrowIfCancellationRequested(); }
        }
        throw new HttpRequestException("Unable to connect to the resolved host.");
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, Uri uri, TimeSpan interval, CancellationToken token)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            ValidateUri(uri);
            if (!visited.Add(uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped))) throw new HttpRequestException("The redirect loop was detected.");
            using var head = new HttpRequestMessage(HttpMethod.Head, uri);
            await WaitRateAsync(interval, token).ConfigureAwait(false);
            var response = await client.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
            {
                response.Dispose();
                using var get = new HttpRequestMessage(HttpMethod.Get, uri);
                await WaitRateAsync(interval, token).ConfigureAwait(false);
                response = await client.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            }
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                using (response)
                {
                    var next = new Uri(uri, location);
                    ValidateUri(next);
                    uri = next;
                }
                continue;
            }
            return response;
        }
        throw new HttpRequestException("The redirect limit was exceeded.");
    }

    private static async Task WaitRateAsync(TimeSpan interval, CancellationToken token)
    {
        await RateGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var delay = LastRequest == 0 ? TimeSpan.Zero : interval - Stopwatch.GetElapsedTime(LastRequest);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, token).ConfigureAwait(false);
            LastRequest = Stopwatch.GetTimestamp();
        }
        finally { RateGate.Release(); }
    }

    private static void ValidateUri(Uri uri)
    {
        if ((uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) || (IPAddress.TryParse(uri.Host, out var address) && !IsPublicAddress(address))) throw new HttpRequestException("The external link URL is invalid or resolves to a non-public address.");
    }

    private static SiteDiagnostic Failure(string url, SiteSourceLocation location, string message) => new("LSQ011", SiteDiagnosticSeverity.Warning, message + " (" + url + ")", location);
    private sealed record CacheDocument(int Version, Dictionary<string, CacheEntry?> Entries);
    private sealed record CacheEntry(DateTimeOffset CheckedAt, int Status, string Message);
    private static bool IsValidCacheEntry(CacheEntry entry) => entry.CheckedAt <= DateTimeOffset.UtcNow && entry.Status is >= 100 and <= 599 && entry.Message is not null;
    private static readonly JsonSerializerOptions CacheJsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static Dictionary<string, CacheEntry?> ReadCache(string path)
    {
        try
        {
            var document = JsonSerializer.Deserialize<CacheDocument>(File.ReadAllText(path), CacheJsonOptions);
            return document is { Version: CacheVersion, Entries: not null } ? document.Entries : new(StringComparer.Ordinal);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(StringComparer.Ordinal);
        }
    }

    private static void WriteCache(string path, Dictionary<string, CacheEntry?> cache)
    {
        var temp = path + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            var valid = cache.Where(static pair => pair.Value is not null && IsValidCacheEntry(pair.Value))
                .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(temp, JsonSerializer.Serialize(new CacheDocument(CacheVersion, valid), CacheJsonOptions));
            File.Move(temp, path, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Cache persistence is optional; the live diagnostics remain valid.
        }
        finally
        {
            try { File.Delete(temp); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
