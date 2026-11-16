namespace LithoSharp.Images;

/// <summary>An sRGB image containing row-major straight-alpha RGBA8 pixels.</summary>
/// <remarks>Operations use premultiplied alpha in sRGB values. Fully transparent output RGB is zero.</remarks>
public sealed class RgbaImage
{
    private readonly byte[] pixels;

    /// <summary>Identifies the pixel rules used by image cache fingerprints.</summary>
    public const string ProcessingProfile = "rgba8-srgb-v1;double;center-bilinear;edge-clamp;premultiplied;half-up;saturate;alpha0-rgb0;source-over";

    /// <summary>Copies the given straight-alpha RGBA8 pixels after checking dimensions and limits.</summary>
    public RgbaImage(int width, int height, ReadOnlySpan<byte> pixels, ImageLimits? limits = null)
    {
        var length = (limits ?? ImageLimits.Default).ValidateDimensions(width, height);
        if (pixels.Length != length)
            throw new ArgumentException("RGBA8 pixels must contain exactly width × height × 4 bytes.", nameof(pixels));
        Width = width;
        Height = height;
        this.pixels = pixels.ToArray();
        NormalizeTransparentPixels(this.pixels);
    }

    internal RgbaImage(int width, int height, byte[] pixels)
    {
        Width = width;
        Height = height;
        this.pixels = pixels;
        NormalizeTransparentPixels(pixels);
    }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }
    /// <summary>Height in pixels.</summary>
    public int Height { get; }
    /// <summary>Row-major straight-alpha RGBA8 pixels. Drawing operations update this memory.</summary>
    public ReadOnlyMemory<byte> Pixels => pixels;

    /// <summary>Creates a solid-color canvas, replacing RGB with zero when alpha is zero.</summary>
    public static RgbaImage Create(int width, int height, byte r, byte g, byte b, byte a = 255, ImageLimits? limits = null)
    {
        var data = new byte[(limits ?? ImageLimits.Default).ValidateDimensions(width, height)];
        var result = new RgbaImage(width, height, data);
        result.FillRectangle(0, 0, width, height, r, g, b, a);
        return result;
    }

    /// <summary>Resizes to the exact width, preserving aspect ratio and rounding height half up, to at least one.</summary>
    public RgbaImage Resize(int width, ImageLimits? limits = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        // Integer arithmetic fixes the aspect-ratio rounding independently of floating-point precision.
        var height = Math.Max(1L, (checked((long)Height * width) + Width / 2L) / Width);
        var data = new byte[(limits ?? ImageLimits.Default).ValidateDimensions(width, height)];
        var outputHeight = (int)height;
        for (var y = 0; y < outputHeight; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceY = Math.Clamp((y + 0.5) * Height / outputHeight - 0.5, 0, Height - 1);
            var y0 = (int)sourceY;
            var y1 = Math.Min(y0 + 1, Height - 1);
            var fy = sourceY - y0;
            for (var x = 0; x < width; x++)
            {
                if ((x & 4095) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                var sourceX = Math.Clamp((x + 0.5) * Width / width - 0.5, 0, Width - 1);
                var x0 = (int)sourceX;
                var x1 = Math.Min(x0 + 1, Width - 1);
                var fx = sourceX - x0;
                var p00 = (y0 * Width + x0) * 4;
                var p10 = (y0 * Width + x1) * 4;
                var p01 = (y1 * Width + x0) * 4;
                var p11 = (y1 * Width + x1) * 4;
                var w00 = (1 - fx) * (1 - fy);
                var w10 = fx * (1 - fy);
                var w01 = (1 - fx) * fy;
                var w11 = fx * fy;
                var alpha = pixels[p00 + 3] * w00 + pixels[p10 + 3] * w10
                    + pixels[p01 + 3] * w01 + pixels[p11 + 3] * w11;
                var destination = (y * width + x) * 4;
                data[destination + 3] = RoundByte(alpha);
                if (data[destination + 3] == 0)
                    continue;
                for (var channel = 0; channel < 3; channel++)
                {
                    var premultiplied = pixels[p00 + channel] * pixels[p00 + 3] * w00
                        + pixels[p10 + channel] * pixels[p10 + 3] * w10
                        + pixels[p01 + channel] * pixels[p01 + 3] * w01
                        + pixels[p11 + channel] * pixels[p11 + 3] * w11;
                    data[destination + channel] = RoundByte(premultiplied / alpha);
                }
            }
        }
        return new RgbaImage(width, outputHeight, data);
    }

    /// <summary>Replaces a rectangle with a solid color. Coordinates are top-left based and clipped to the canvas.</summary>
    /// <remarks>Zero-sized rectangles are empty; negative sizes are rejected. This operation replaces pixels.</remarks>
    public void FillRectangle(int x, int y, int width, int height, byte r, byte g, byte b, byte a = 255,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        if (a == 0)
            r = g = b = 0;
        var left = Math.Max(0L, x);
        var top = Math.Max(0L, y);
        var right = Math.Min(Width, (long)x + width);
        var bottom = Math.Min(Height, (long)y + height);
        for (var row = top; row < bottom; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var column = left; column < right; column++)
            {
                if ((column & 4095) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                var index = (int)((row * Width + column) * 4);
                pixels[index] = r;
                pixels[index + 1] = g;
                pixels[index + 2] = b;
                pixels[index + 3] = a;
            }
        }
    }

    /// <summary>Places a source image using source-over alpha compositing, clipping it to this canvas.</summary>
    /// <remarks>Coordinates are top-left based. Placing the canvas over itself uses a snapshot of its original pixels.</remarks>
    public void Composite(RgbaImage source, int x, int y, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        var left = Math.Max(0L, x);
        var top = Math.Max(0L, y);
        var right = Math.Min(Width, (long)x + source.Width);
        var bottom = Math.Min(Height, (long)y + source.Height);
        if (left >= right || top >= bottom)
            return;
        var sourcePixels = ReferenceEquals(this, source) ? source.pixels.ToArray() : source.pixels;
        for (var row = top; row < bottom; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var column = left; column < right; column++)
            {
                if ((column & 4095) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                var from = (int)(((row - y) * source.Width + column - x) * 4);
                var to = (int)((row * Width + column) * 4);
                var sourceAlpha = sourcePixels[from + 3] / 255.0;
                var retainedAlpha = pixels[to + 3] * (1 - sourceAlpha);
                var alpha = sourcePixels[from + 3] + retainedAlpha;
                for (var channel = 0; channel < 3; channel++)
                    pixels[to + channel] = alpha == 0 ? (byte)0 : RoundByte(
                        (sourcePixels[from + channel] * sourcePixels[from + 3] + pixels[to + channel] * retainedAlpha) / alpha);
                pixels[to + 3] = RoundByte(alpha);
            }
        }
    }

    private static byte RoundByte(double value) => (byte)Math.Clamp(Math.Floor(value + 0.5), 0, 255);

    private static void NormalizeTransparentPixels(Span<byte> data)
    {
        for (var i = 0; i < data.Length; i += 4)
            if (data[i + 3] == 0)
                data[i] = data[i + 1] = data[i + 2] = 0;
    }
}
