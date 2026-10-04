using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using SkiaSharp;

namespace LithoSharp;

internal sealed class SocialImageGenerator
{
    private const int Width = 1200;
    private const int Height = 630;
    private const float IconSize = 208f;
    private const float IconLeft = 50f;
    private const float IconTop = 211f;
    private const float TextLeft = 278f;
    private static readonly SKColor BackgroundColor = new(22, 27, 34);
    private static readonly SKColor AccentColor = new(88, 166, 255);
    private static readonly SKColor TitleColor = new(240, 246, 252);
    private static readonly SKColor DoneColor = new(188, 140, 255);
    private static readonly string[] PreferredMonoFonts =
    [
        "Consolas",
        "Cascadia Code",
        "DejaVu Sans Mono",
        "Liberation Mono",
        "Courier New"
    ];
    private static readonly object FontGate = new();
    private static FontPair? _capturedFonts;
    private static readonly string[] LinuxDefaultFamilies =
        ["Arial", "Verdana", "Times New Roman", "Droid Sans", "DejaVu Serif"];
    private sealed record InstalledFont(
        string Path, int Index, string Family, int Weight, int Width, SKFontStyleSlant Slant);
    private sealed record CapturedFont(
        byte[] Bytes, int Index, string Family, int Weight, int Width, SKFontStyleSlant Slant,
        SKFontVariationPositionCoordinate[] Position)
    {
        public SKTypeface Open()
        {
            using var data = SKData.CreateCopy(Bytes);
            var typeface = SKTypeface.FromData(data, Index)
                ?? throw new InvalidOperationException("Failed to load captured social image font.");
            if (Position.Length == 0) return typeface;
            try
            {
                return typeface.Clone(new SKFontArguments
                {
                    CollectionIndex = Index, VariationDesignPosition = Position,
                }) ?? throw new InvalidOperationException("Failed to restore captured font variations.");
            }
            finally
            {
                typeface.Dispose();
            }
        }
    }
    private sealed record FontPair(CapturedFont Bold, CapturedFont Normal);
    private readonly byte[] _siteIconBytes;
    private readonly FontPair _fonts;

    public SocialImageGenerator(byte[] siteIconBytes) : this(siteIconBytes, GetFonts()) { }

    internal SocialImageGenerator(byte[] siteIconBytes, SKTypeface bold, SKTypeface normal)
        : this(siteIconBytes, new FontPair(CaptureFont(bold), CaptureFont(normal))) { }

    private SocialImageGenerator(byte[] siteIconBytes, FontPair fonts)
    {
        _siteIconBytes = siteIconBytes.Length > 0 ? siteIconBytes
            : throw new ArgumentException("Site icon bytes must not be empty.", nameof(siteIconBytes));
        _fonts = fonts;
    }

    private static FontPair GetFonts()
    {
        lock (FontGate)
        {
            if (_capturedFonts is not null) return _capturedFonts;
            // Like the old native manager, successful selection lasts for this process.
            // Retain immutable bytes, not live file paths or native handles. Failed
            // discovery is not cached, so installing a usable font permits a retry.
            var installed = new Lazy<InstalledFont[]>(FindLinuxFonts);
            return _capturedFonts = new FontPair(
                ResolveSystemFont(isBold: true, installed),
                ResolveSystemFont(isBold: false, installed));
        }
    }

    internal static SKTypeface ResolveTypeface(bool isBold) =>
        (isBold ? GetFonts().Bold : GetFonts().Normal).Open();

    internal string GetFontImplementationFingerprint() => Fingerprint(_fonts);

    private static CapturedFont CaptureFont(SKTypeface typeface)
    {
        using var stream = typeface.OpenStream(out var collectionIndex);
        if (stream is null || stream.Length <= 0 || typeface.IsEmpty)
            throw new InvalidOperationException("Social image text requires a readable, nonempty font.");
        var bytes = new byte[stream.Length];
        var buffer = new byte[Math.Min(81920, bytes.Length)];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var count = stream.Read(buffer, Math.Min(buffer.Length, bytes.Length - offset));
            if (count <= 0) throw new IOException("Could not capture the complete social image font.");
            buffer.AsSpan(0, count).CopyTo(bytes.AsSpan(offset));
            offset += count;
        }

        // Inspect the same immutable data that rendering will reopen, not stale file metadata.
        var snapshot = new CapturedFont(bytes, collectionIndex, typeface.FamilyName,
            typeface.FontWeight, typeface.FontWidth, typeface.FontSlant, typeface.VariationDesignPosition);
        using var captured = snapshot.Open();
        return snapshot with
        {
            Family = captured.FamilyName, Weight = captured.FontWeight,
            Width = captured.FontWidth, Slant = captured.FontSlant,
        };
    }

    internal static string? GetImplementationFingerprint()
    {
        try
        {
            return Fingerprint(GetFonts());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException or NotSupportedException or ExternalException
            or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
            or TypeInitializationException)
        {
            return null;
        }
    }

    private static string Fingerprint(FontPair fonts) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            RendererModule = typeof(SocialImageGenerator).Module.ModuleVersionId,
            ManagedAssembly = typeof(SKTypeface).Assembly.FullName,
            ManagedModule = typeof(SKTypeface).Module.ModuleVersionId,
            NativeVersion = SkiaSharpVersion.Native.ToString(),
            RuntimeInformation.RuntimeIdentifier,
            RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture,
            RuntimeInformation.ProcessArchitecture,
            RuntimeInformation.FrameworkDescription,
            BoldFont = FontFingerprint(fonts.Bold),
            NormalFont = FontFingerprint(fonts.Normal),
        })));

    private static object FontFingerprint(CapturedFont font) => new
    {
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(font.Bytes)),
        CollectionIndex = font.Index,
        FamilyName = font.Family,
        FontWeight = font.Weight,
        FontWidth = font.Width,
        FontSlant = font.Slant,
        VariationPosition = font.Position.Select(position => new { position.Axis, position.Value }),
    };

    public Task<byte[]> BuildSiteImageAsync(string siteTitle, string siteDescription, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteTitle);
        ArgumentException.ThrowIfNullOrWhiteSpace(siteDescription);

        return BuildImageAsync(siteTitle, siteDescription, AccentColor, 62, 50, 214, 314, cancellationToken);
    }

    public Task<byte[]> BuildPostImageAsync(string siteTitle, string postTitle, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteTitle);
        ArgumentException.ThrowIfNullOrWhiteSpace(postTitle);

        return BuildImageAsync(siteTitle, postTitle, DoneColor, 62, 62, 214, 314, cancellationToken);
    }

    private Task<byte[]> BuildImageAsync(
        string title,
        string subtitle,
        SKColor subtitleColor,
        float titleFontSize,
        float subtitleFontSize,
        float titleTop,
        float subtitleTop,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var surface = SKSurface.Create(new SKImageInfo(Width, Height))
            ?? throw new InvalidOperationException("Failed to create a Skia surface for social image generation.");
        var canvas = surface.Canvas;
        canvas.Clear(BackgroundColor);
        DrawBrandIcon(canvas);

        using var siteTitleTypeface = _fonts.Bold.Open();
        using var subtitleTypeface = _fonts.Normal.Open();
        using var siteTitleFont = CreateFont(siteTitleTypeface, titleFontSize);
        using var subtitleFont = CreateFont(subtitleTypeface, subtitleFontSize);
        using var siteTitlePaint = CreatePaint(TitleColor);
        using var subtitlePaint = CreatePaint(subtitleColor);

        DrawText(canvas, title, TextLeft, titleTop, siteTitleFont, siteTitlePaint);
        DrawText(canvas, subtitle, TextLeft, subtitleTop, subtitleFont, subtitlePaint);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, quality: 100)
            ?? throw new InvalidOperationException("Failed to encode the generated social image as PNG.");

        return Task.FromResult(data.ToArray());
    }

    private void DrawBrandIcon(SKCanvas canvas)
    {
        using var icon = SKBitmap.Decode(_siteIconBytes)
            ?? throw new InvalidOperationException("Failed to decode the favicon image for social image generation.");
        canvas.DrawBitmap(icon, new SKRect(IconLeft, IconTop, IconLeft + IconSize, IconTop + IconSize), SKSamplingOptions.Default);
    }

    private static void DrawText(SKCanvas canvas, string text, float left, float top, SKFont font, SKPaint paint)
    {
        var metrics = font.Metrics;
        canvas.DrawText(text, left, top - metrics.Ascent, SKTextAlign.Left, font, paint);
    }

    private static SKFont CreateFont(SKTypeface typeface, float size) =>
        new(typeface, size)
        {
            Subpixel = true
        };

    private static SKPaint CreatePaint(SKColor color)
    {
        return new SKPaint
        {
            Color = color,
            IsAntialias = true
        };
    }

    private static CapturedFont ResolveSystemFont(bool isBold, Lazy<InstalledFont[]> installed)
    {
        var fontStyle = isBold ? SKFontStyle.Bold : SKFontStyle.Normal;
        foreach (var preferredFont in PreferredMonoFonts)
        {
            var typeface = SKTypeface.FromFamilyName(preferredFont, fontStyle);
            if (typeface is null) continue;
            try
            {
                if (!typeface.IsEmpty) return CaptureFont(typeface);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or ArgumentException or InvalidOperationException or NotSupportedException or ExternalException)
            {
                // Try another candidate when a native face cannot be captured coherently.
            }
            finally
            {
                if (!ReferenceEquals(typeface, SKTypeface.Default)) typeface.Dispose();
            }
        }

        if (!OperatingSystem.IsLinux())
            throw new InvalidOperationException("Social image text requires a readable, nonempty font.");

        var fonts = installed.Value;

        // Missing-family lookup previously selected the native default immediately.
        // Preserve its family priorities rather than choosing a different mono font.
        var families = new[] { PreferredMonoFonts[0] }.Concat(LinuxDefaultFamilies)
            .Concat(fonts.Select(font => font.Family)).Distinct(StringComparer.Ordinal);
        foreach (var family in families)
        {
            foreach (var font in fonts.Where(font => font.Family == family)
                         .OrderBy(font => font.Width <= 5 ? 5 - font.Width : font.Width)
                         .ThenBy(font => font.Slant == SKFontStyleSlant.Upright ? 0
                             : font.Slant == SKFontStyleSlant.Oblique ? 1 : 2)
                         .ThenBy(font => isBold
                             ? font.Weight >= 700 ? font.Weight - 700 : 2000 - font.Weight
                             : font.Weight is >= 400 and <= 500 ? font.Weight - 400
                                 : font.Weight < 400 ? 1000 - font.Weight : 1000 + font.Weight))
            {
                using var typeface = OpenInstalledFont(font.Path, font.Index);
                if (typeface is null) continue;
                try
                {
                    var captured = CaptureFont(typeface);
                    if (captured.Index == font.Index && captured.Family == font.Family
                        && captured.Weight == font.Weight && captured.Width == font.Width
                        && captured.Slant == font.Slant) return captured;
                    // A replaced file must not freeze a different family/style under stale metadata.
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                    or ArgumentException or InvalidOperationException or NotSupportedException or ExternalException)
                {
                    // An unreadable candidate does not prevent trying another installed font.
                }
            }
        }

        throw new InvalidOperationException(
            "Social image text requires an installed font under /usr/share/fonts on Linux.");
    }

    private static InstalledFont[] FindLinuxFonts()
    {
        const string root = "/usr/share/fonts";
        if (!Directory.Exists(root)) return [];
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.ReparsePoint,
            MaxRecursionDepth = 16,
        };
        var fonts = new List<InstalledFont>();
        // Fixed system root, finite depth and collection size; never inspect user files
        // or follow directory links. Native 3.x used this same root and font formats.
        foreach (var path in Directory.EnumerateFiles(root, "*", options)
                     .Where(path => Path.GetExtension(path).ToLowerInvariant()
                         is ".ttf" or ".ttc" or ".otf" or ".pfb")
                     .Order(StringComparer.Ordinal))
        {
            for (var index = 0; index < 64; index++)
            {
                using var typeface = OpenInstalledFont(path, index);
                if (typeface is null) break;
                fonts.Add(new InstalledFont(path, index, typeface.FamilyName,
                    typeface.FontWeight, typeface.FontWidth, typeface.FontSlant));
            }
        }

        return fonts.ToArray();
    }

    private static SKTypeface? OpenInstalledFont(string path, int index)
    {
        try
        {
            var typeface = SKTypeface.FromFile(path, index);
            if (typeface is not null && !typeface.IsEmpty) return typeface;
            typeface?.Dispose();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or InvalidOperationException or NotSupportedException
            or ExternalException)
        {
            // An unreadable/unsupported installed font must not hide another usable face.
        }

        return null;
    }
}
