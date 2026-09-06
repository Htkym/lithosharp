using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace LithoSharp.Routing;

/// <summary>
/// 公開 URL の正規化済みパスと、出力ルートからの相対ファイルパスを一体として表します。
/// </summary>
#if NETSTANDARD2_0
internal sealed class SiteRoute : IEquatable<SiteRoute>
#else
public sealed class SiteRoute : IEquatable<SiteRoute>
#endif
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly char[] SlashSeparator = ['/'];
    private static readonly char[] DotSeparator = ['.'];

    private SiteRoute(string publicPath, string relativeOutputPath)
    {
        PublicPath = publicPath;
        RelativeOutputPath = relativeOutputPath;
    }

    /// <summary>先頭が <c>/</c> で始まる、パーセントエンコード済みの公開 URL パスを取得します。</summary>
    public string PublicPath { get; }

    /// <summary><c>/</c> 区切りで正規化された、出力ルートからの相対ファイルパスを取得します。</summary>
    public string RelativeOutputPath { get; }

    /// <summary>ファイル形式のルートを作成します。</summary>
    /// <param name="relativePath">サイト内の相対 URL パス。</param>
    /// <param name="baseUrl">
    /// サイトの絶対 HTTP(S) ベース URL。<see langword="null"/> の場合はサイトルートを使用します。
    /// </param>
    /// <returns>正規化と検証が完了したルート。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="relativePath"/> が <see langword="null"/> です。</exception>
    /// <exception cref="ArgumentException">
    /// 相対パスが空、絶対パス、ディレクトリ形式、ドットセグメントを含む、または安全でないファイル名を含みます。
    /// </exception>
    /// <exception cref="UriFormatException">
    /// パーセントエンコードまたは <paramref name="baseUrl"/> の URI 形式が不正です。
    /// </exception>
    public static SiteRoute ForFile(string relativePath, string? baseUrl = null)
    {
        if (relativePath is null) throw new ArgumentNullException(nameof(relativePath));

        if (relativePath.Length == 0)
        {
            throw new ArgumentException("A file route must not be empty.", nameof(relativePath));
        }

        if (IsOnlySeparators(relativePath))
        {
            throw new ArgumentException("A file route must name a relative file.", nameof(relativePath));
        }

        if (EndsWithSeparator(relativePath))
        {
            throw new ArgumentException("A file route must not end with a separator.", nameof(relativePath));
        }

        var route = NormalizeRelativePath(relativePath, nameof(relativePath));
        var basePath = NormalizeBaseUrlPath(baseUrl);
        return new SiteRoute(
            BuildPublicPath(basePath, route.EncodedPath, trailingSlash: false),
            route.OutputPath);
    }

    /// <summary>末尾スラッシュの公開 URL と <c>index.html</c> 出力を持つディレクトリ形式のルートを作成します。</summary>
    /// <param name="relativePath">サイト内の相対ディレクトリパス。空文字列または <c>/</c> はサイトルートを表します。</param>
    /// <param name="baseUrl">
    /// サイトの絶対 HTTP(S) ベース URL。<see langword="null"/> の場合はサイトルートを使用します。
    /// </param>
    /// <returns>正規化と検証が完了したルート。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="relativePath"/> が <see langword="null"/> です。</exception>
    /// <exception cref="ArgumentException">
    /// 相対パスが絶対パス、ドットセグメントを含む、または安全でないディレクトリ名を含みます。
    /// </exception>
    /// <exception cref="UriFormatException">
    /// パーセントエンコードまたは <paramref name="baseUrl"/> の URI 形式が不正です。
    /// </exception>
    public static SiteRoute ForDirectoryIndex(string relativePath, string? baseUrl = null)
    {
        if (relativePath is null) throw new ArgumentNullException(nameof(relativePath));

        var isRoot = relativePath.Length == 0 || IsOnlySeparators(relativePath);
        var route = isRoot
            ? NormalizedPath.Empty
            : NormalizeRelativePath(relativePath, nameof(relativePath));
        var basePath = NormalizeBaseUrlPath(baseUrl);
        var outputPath = route.OutputPath.Length == 0
            ? "index.html"
            : $"{route.OutputPath}/index.html";

        return new SiteRoute(
            BuildPublicPath(basePath, route.EncodedPath, trailingSlash: true),
            outputPath);
    }

    /// <summary>指定したルートが同じ正規化済みルートを表すかどうかを判定します。</summary>
    /// <param name="other">比較するルート。</param>
    /// <returns>正規化済みの公開パスと出力パスが一致する場合は <see langword="true"/>。</returns>
    public bool Equals(SiteRoute? other) =>
        other is not null
        && StringComparer.Ordinal.Equals(PublicPath, other.PublicPath)
        && StringComparer.Ordinal.Equals(RelativeOutputPath, other.RelativeOutputPath);

    /// <summary>指定したオブジェクトが同じ正規化済みルートを表すかどうかを判定します。</summary>
    /// <param name="obj">比較するオブジェクト。</param>
    /// <returns>同じルートを表す場合は <see langword="true"/>。</returns>
    public override bool Equals(object? obj) => obj is SiteRoute other && Equals(other);

    /// <summary>正規化済みルートの大文字と小文字を区別するハッシュコードを返します。</summary>
    /// <returns>正規化済みの公開パスと出力パスに基づくハッシュコード。</returns>
#if NETSTANDARD2_0
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(PublicPath) ^ StringComparer.Ordinal.GetHashCode(RelativeOutputPath);
#else
    public override int GetHashCode() =>
        HashCode.Combine(
            StringComparer.Ordinal.GetHashCode(PublicPath),
            StringComparer.Ordinal.GetHashCode(RelativeOutputPath));
#endif

    internal SiteRoute WithBaseUrl(string baseUrl)
    {
        if (baseUrl is null) throw new ArgumentNullException(nameof(baseUrl));
        var encodedOutputPath = string.Join("/", RelativeOutputPath.Split('/').Select(Uri.EscapeDataString));
        if (PublicPath.EndsWith("/", StringComparison.Ordinal))
        {
            var relativeDirectory = RelativeOutputPath == "index.html"
                ? string.Empty
                : encodedOutputPath.Substring(0, encodedOutputPath.Length - "/index.html".Length);
            return ForDirectoryIndex(relativeDirectory, baseUrl);
        }

        return ForFile(encodedOutputPath, baseUrl);
    }

    internal static string NormalizeRelativeOutputPath(string relativeOutputPath)
    {
        if (relativeOutputPath is null) throw new ArgumentNullException(nameof(relativeOutputPath));

        if (relativeOutputPath.Length == 0)
        {
            throw new ArgumentException(
                "A relative output path must not be empty.",
                nameof(relativeOutputPath));
        }

        if (IsOnlySeparators(relativeOutputPath))
        {
            throw new ArgumentException(
                "A relative output path must name a file.",
                nameof(relativeOutputPath));
        }

        if (EndsWithSeparator(relativeOutputPath))
        {
            throw new ArgumentException(
                "A relative output path must not end with a separator.",
                nameof(relativeOutputPath));
        }

        if (LooksRooted(relativeOutputPath))
        {
            throw new ArgumentException(
                "An output path must be relative.",
                nameof(relativeOutputPath));
        }

        var segments = relativeOutputPath
            .Replace('\\', '/')
            .Split(SlashSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => NormalizeAndValidateSegment(segment, nameof(relativeOutputPath)));
        return string.Join("/", segments);
    }

    private static NormalizedPath NormalizeRelativePath(string path, string parameterName)
    {
        if (LooksRooted(path))
        {
            throw new ArgumentException("A site route must be a relative path.", parameterName);
        }

        if (path.IndexOfAny(['?', '#']) >= 0)
        {
            throw new ArgumentException("A site route must not contain a query string or fragment.", parameterName);
        }

        var decodedSegments = new List<string>();
        var encodedSegments = new List<string>();
        foreach (var rawSegment in path.Replace('\\', '/').Split(SlashSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = NormalizeAndValidateSegment(
                DecodeSegment(rawSegment, parameterName),
                parameterName);
            decodedSegments.Add(segment);
            encodedSegments.Add(Uri.EscapeDataString(segment));
        }

        if (decodedSegments.Count == 0)
        {
            throw new ArgumentException("A site route must name a relative path.", parameterName);
        }

        return new NormalizedPath(
            string.Join("/", encodedSegments),
            string.Join("/", decodedSegments));
    }

    private static string NormalizeBaseUrlPath(string? baseUrl)
    {
        if (baseUrl is null)
        {
            return "";
        }

        if (baseUrl.Length == 0)
        {
            throw new UriFormatException("The base URL must be an absolute HTTP or HTTPS URI.");
        }

        if (baseUrl.IndexOf('\\') >= 0)
        {
            throw new UriFormatException("The base URL must use '/' as its URI path separator.");
        }

        ValidatePercentEncoding(baseUrl, nameof(baseUrl));
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !uri.IsWellFormedOriginalString())
        {
            throw new UriFormatException("The base URL must be an absolute HTTP or HTTPS URI.");
        }

        if (baseUrl.IndexOfAny(['?', '#']) >= 0)
        {
            throw new UriFormatException("The base URL must not contain a query string or fragment.");
        }

        var rawPath = ExtractRawUriPath(baseUrl);
        if (rawPath.Length == 0)
        {
            return "";
        }

        try
        {
            ValidateBasePath(rawPath);
        }
        catch (ArgumentException exception)
        {
            throw new UriFormatException("The base URL contains an unsafe or ambiguous path segment.", exception);
        }

        var escapedPath = $"/{uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped)}";
        return escapedPath.EndsWith("/", StringComparison.Ordinal) ? escapedPath : $"{escapedPath}/";
    }

    private static void ValidateBasePath(string rawPath)
    {
        if (rawPath.StartsWith("//", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A base URL path must not produce a network-path reference.",
                nameof(rawPath));
        }

        foreach (var rawSegment in rawPath.Split(SlashSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = DecodeSegment(rawSegment, nameof(rawPath));
            if (segment is "." or "..")
            {
                throw new ArgumentException(
                    "A base URL must not contain '.' or '..' path segments.",
                    nameof(rawPath));
            }

            foreach (var character in segment)
            {
                if (character is '/' or '\\' || char.IsControl(character))
                {
                    throw new ArgumentException(
                        $"The base URL path segment '{segment}' contains an unsafe character.",
                        nameof(rawPath));
                }
            }
        }
    }

    private static string ExtractRawUriPath(string baseUrl)
    {
        var authorityStart = baseUrl.IndexOf("://", StringComparison.Ordinal);
        var pathStart = baseUrl.IndexOf('/', authorityStart + 3);
        if (pathStart < 0)
        {
            return "";
        }

        var pathEnd = baseUrl.IndexOfAny(['?', '#'], pathStart);
        return pathEnd < 0
            ? baseUrl.Substring(pathStart)
            : baseUrl.Substring(pathStart, pathEnd - pathStart);
    }

    private static string DecodeSegment(string segment, string parameterName)
    {
        ValidatePercentEncoding(segment, parameterName);

        var bytes = new List<byte>(segment.Length);
        var literalStart = 0;
        for (var index = 0; index < segment.Length;)
        {
            if (segment[index] != '%')
            {
                index++;
                continue;
            }

            AppendUtf8(bytes, segment.AsSpan(literalStart, index - literalStart), parameterName);
            bytes.Add((byte)((HexValue(segment[index + 1]) << 4) | HexValue(segment[index + 2])));
            index += 3;
            literalStart = index;
        }

        AppendUtf8(bytes, segment.AsSpan(literalStart), parameterName);
        try
        {
            return StrictUtf8.GetString(bytes.ToArray());
        }
        catch (DecoderFallbackException exception)
        {
            throw new UriFormatException(
                $"The path segment '{segment}' contains invalid UTF-8 percent encoding.",
                exception);
        }
    }

    private static void AppendUtf8(List<byte> bytes, ReadOnlySpan<char> value, string parameterName)
    {
        if (value.Length == 0)
        {
            return;
        }

        try
        {
            bytes.AddRange(StrictUtf8.GetBytes(value.ToString()));
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("A site route contains an invalid Unicode sequence.", parameterName, exception);
        }
    }

    private static void ValidatePercentEncoding(string value, string parameterName)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '%')
            {
                continue;
            }

            if (index + 2 >= value.Length
                || HexValue(value[index + 1]) < 0
                || HexValue(value[index + 2]) < 0)
            {
                throw new UriFormatException(
                    $"The value for '{parameterName}' contains an invalid percent escape at index {index}.");
            }

            index += 2;
        }
    }

    private static string NormalizeAndValidateSegment(string segment, string parameterName)
    {
        var normalizedSegment = segment.Normalize(NormalizationForm.FormC);
        ValidateSegment(normalizedSegment, parameterName);
        return normalizedSegment;
    }

    private static void ValidateSegment(string segment, string parameterName)
    {
        if (segment is "." or "..")
        {
            throw new ArgumentException("A site route must not contain '.' or '..' path segments.", parameterName);
        }

        if (segment.EndsWith(" ", StringComparison.Ordinal) || segment.EndsWith(".", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The path segment '{segment}' must not end with a space or period.",
                parameterName);
        }

        foreach (var character in segment)
        {
            if (character < ' '
                || character is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*'
                || char.IsControl(character))
            {
                throw new ArgumentException(
                    $"The path segment '{segment}' contains a platform-unsafe character.",
                    parameterName);
            }
        }

        var deviceName = segment.Split(DotSeparator, 2)[0];
        if (IsReservedDeviceName(deviceName))
        {
            throw new ArgumentException(
                $"The path segment '{segment}' uses a platform-reserved device name.",
                parameterName);
        }
    }

    private static bool IsReservedDeviceName(string value)
    {
        if (value.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || value.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || value.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || value.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || value.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase)
            || value.Equals("CONIN$", StringComparison.OrdinalIgnoreCase)
            || value.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (value.Length == 4
            && (value.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)))
        {
            return value[3] is >= '0' and <= '9' or '¹' or '²' or '³';
        }

        return false;
    }

    private static string BuildPublicPath(string basePath, string routePath, bool trailingSlash)
    {
        var publicPath = $"{(basePath.Length == 0 ? "/" : basePath)}{routePath}";
        return trailingSlash && !publicPath.EndsWith("/", StringComparison.Ordinal) ? $"{publicPath}/" : publicPath;
    }

    private static bool LooksRooted(string value) =>
        value.Length != 0
        && (value[0] is '/' or '\\'
            || (value.Length >= 2 && (value[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z') && value[1] == ':')
            || value.IndexOf("://", StringComparison.Ordinal) >= 0);

    private static bool EndsWithSeparator(string value) => value[value.Length - 1] is '/' or '\\';

    private static bool IsOnlySeparators(string value)
    {
        foreach (var character in value)
        {
            if (character is not ('/' or '\\'))
            {
                return false;
            }
        }

        return value.Length != 0;
    }

    private static int HexValue(char value) =>
        value switch
        {
            >= '0' and <= '9' => value - '0',
            >= 'A' and <= 'F' => value - 'A' + 10,
            >= 'a' and <= 'f' => value - 'a' + 10,
            _ => -1,
        };

    private readonly struct NormalizedPath
    {
        public NormalizedPath(string encodedPath, string outputPath)
        {
            EncodedPath = encodedPath;
            OutputPath = outputPath;
        }
        public string EncodedPath { get; }
        public string OutputPath { get; }
        public static NormalizedPath Empty { get; } = new("", "");
    }
}
