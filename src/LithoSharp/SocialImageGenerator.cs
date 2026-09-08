using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using SkiaSharp;

namespace LithoSharp;

internal sealed class SocialImageGenerator(byte[] siteIconBytes)
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
    private readonly byte[] _siteIconBytes = siteIconBytes.Length > 0 ? siteIconBytes : throw new ArgumentException("Site icon bytes must not be empty.", nameof(siteIconBytes));

    internal static string? GetImplementationFingerprint()
    {
        try
        {
            var bold = GetTypefaceFingerprint(isBold: true);
            var normal = GetTypefaceFingerprint(isBold: false);
            if (bold is null || normal is null) return null;
            return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
            {
                ManagedAssembly = typeof(SKTypeface).Assembly.FullName,
                ManagedModule = typeof(SKTypeface).Module.ModuleVersionId,
                NativeVersion = SkiaSharpVersion.Native.ToString(),
                RuntimeInformation.RuntimeIdentifier,
                RuntimeInformation.OSDescription,
                RuntimeInformation.OSArchitecture,
                RuntimeInformation.ProcessArchitecture,
                RuntimeInformation.FrameworkDescription,
                BoldFont = bold,
                NormalFont = normal,
            })));
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException
            or ArgumentException or NotSupportedException or ExternalException
            or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
            or TypeInitializationException)
        {
            return null;
        }
    }

    private static string? GetTypefaceFingerprint(bool isBold)
    {
        var typeface = ResolveTypeface(isBold);
        try
        {
            using var stream = typeface.OpenStream(out var collectionIndex);
            if (stream is null || stream.Length <= 0) return null;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            var remaining = stream.Length;
            while (remaining > 0)
            {
                var count = stream.Read(buffer, Math.Min(buffer.Length, remaining));
                if (count <= 0) return null;
                hash.AppendData(buffer, 0, count);
                remaining -= count;
            }
            return JsonSerializer.Serialize(new
            {
                Sha256 = Convert.ToHexStringLower(hash.GetHashAndReset()),
                CollectionIndex = collectionIndex,
                typeface.FamilyName,
                typeface.FontWeight,
                typeface.FontWidth,
                typeface.FontSlant,
            });
        }
        finally
        {
            if (!ReferenceEquals(typeface, SKTypeface.Default)) typeface.Dispose();
        }
    }

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

        using var siteTitleTypeface = ResolveTypeface(isBold: true);
        using var subtitleTypeface = ResolveTypeface(isBold: false);
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
        canvas.DrawBitmap(icon, new SKRect(IconLeft, IconTop, IconLeft + IconSize, IconTop + IconSize));
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

    private static SKTypeface ResolveTypeface(bool isBold)
    {
        var fontStyle = isBold ? SKFontStyle.Bold : SKFontStyle.Normal;
        foreach (var preferredFont in PreferredMonoFonts)
        {
            var typeface = SKTypeface.FromFamilyName(preferredFont, fontStyle);
            if (typeface is not null)
            {
                return typeface;
            }
        }

        return SKTypeface.Default;
    }
}
