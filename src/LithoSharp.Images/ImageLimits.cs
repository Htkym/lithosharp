namespace LithoSharp.Images;

/// <summary>Allocation and input limits for the limited PNG and RGBA8 implementation.</summary>
public sealed class ImageLimits
{
    /// <summary>The default limits. Instances are immutable after initialization.</summary>
    public static ImageLimits Default { get; } = new();

    /// <summary>Maximum PNG datastream size, including metadata, in bytes.</summary>
    public long MaxInputBytes { get; init; } = 64L * 1024 * 1024;
    /// <summary>Maximum data length of one PNG chunk.</summary>
    public int MaxChunkBytes { get; init; } = 32 * 1024 * 1024;
    /// <summary>Maximum number of PNG chunks, including empty chunks.</summary>
    public int MaxChunkCount { get; init; } = 10_000;
    /// <summary>Maximum image width.</summary>
    public int MaxWidth { get; init; } = 16_384;
    /// <summary>Maximum image height.</summary>
    public int MaxHeight { get; init; } = 16_384;
    /// <summary>Maximum number of image pixels.</summary>
    public long MaxPixels { get; init; } = 32L * 1024 * 1024;
    /// <summary>Maximum size of an RGBA8 buffer or the decompressed PNG scanlines.</summary>
    public long MaxDecodedBytes { get; init; } = 128L * 1024 * 1024;

    internal void Validate()
    {
        if (MaxInputBytes <= 0 || MaxChunkBytes <= 0 || MaxChunkCount <= 0 || MaxWidth <= 0
            || MaxHeight <= 0 || MaxPixels <= 0 || MaxDecodedBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(ImageLimits), "Image limits must be positive.");
    }

    internal int ValidateDimensions(long width, long height)
    {
        Validate();
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Image dimensions must be positive.");
        if (width > MaxWidth || height > MaxHeight)
            throw new ImageLimitException("Image width or height exceeds the configured dimension limit.");
        var pixels = checked(width * height);
        if (pixels > MaxPixels)
            throw new ImageLimitException("Image pixel count exceeds the configured limit.");
        if (pixels > MaxDecodedBytes / 4 || pixels > Array.MaxLength / 4)
            throw new ImageLimitException("Image RGBA8 bytes exceed the configured allocation limit.");
        return (int)(pixels * 4);
    }
}

/// <summary>An image was stopped because it exceeds a configured resource limit.</summary>
public sealed class ImageLimitException : IOException
{
    /// <summary>Creates a resource-limit failure with its cause.</summary>
    public ImageLimitException(string message) : base(message) { }
}
