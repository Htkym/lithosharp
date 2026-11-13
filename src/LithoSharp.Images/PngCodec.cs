using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace LithoSharp.Images;

/// <summary>Reads limited 8-bit noninterlaced sRGB PNGs and writes noninterlaced RGBA8 PNGs.</summary>
/// <remarks>Supports color types 0, 2, 4 and 6. Palette, animation, EXIF and non-sRGB color management require pre-generated assets.</remarks>
public static class PngCodec
{
    /// <summary>Identifies the PNG profile and encoding rules used by cache fingerprints.</summary>
    public const string Profile = "png-v1;8bit;noninterlaced;gray-rgb-grayalpha-rgba;srgb-or-untagged;rgba8-output;filter0;zlib";

    private static ReadOnlySpan<byte> Signature => [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly uint[] CrcTable = CreateCrcTable();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Reads a bounded PNG stream. The caller retains ownership of the stream.</summary>
    /// <exception cref="InvalidDataException">The PNG structure, checksum or compressed image data is invalid.</exception>
    /// <exception cref="NotSupportedException">The PNG requests features outside the limited profile.</exception>
    /// <exception cref="ImageLimitException">A configured resource limit is exceeded.</exception>
    public static RgbaImage Decode(Stream input, ImageLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();
        limits ??= ImageLimits.Default;
        limits.Validate();
        using var buffer = new MemoryStream();
        var block = new byte[8192];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Read one extra byte at the boundary so a stream exactly at its limit is valid.
            var allowance = Math.Min(block.Length, Math.Min(limits.MaxInputBytes, Array.MaxLength) - buffer.Length + 1);
            var read = input.Read(block, 0, (int)allowance);
            if (read == 0)
                break;
            if (buffer.Length + read > limits.MaxInputBytes || buffer.Length + read > Array.MaxLength)
                throw new ImageLimitException("PNG input bytes exceed the configured limit.");
            buffer.Write(block, 0, read);
        }
        return Decode(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), limits, cancellationToken);
    }

    /// <summary>Decodes a limited PNG to RGBA8, validating every chunk CRC before using its contents.</summary>
    public static RgbaImage Decode(ReadOnlySpan<byte> input, ImageLimits? limits = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        limits ??= ImageLimits.Default;
        limits.Validate();
        if (input.Length > limits.MaxInputBytes)
            throw new ImageLimitException("PNG input bytes exceed the configured limit.");
        if (input.Length < 8 || !input[..8].SequenceEqual(Signature))
            throw Invalid("PNG signature is missing or invalid.");

        var width = 0;
        var height = 0;
        var channels = 0;
        var color = 0;
        var outputLength = 0;
        var scanlineLength = 0;
        var offset = 8;
        var chunkCount = 0;
        var sawHeader = false;
        var sawData = false;
        var endedData = false;
        var sawEnd = false;
        var paletteEntries = 0;
        byte[]? transparent = null;
        var uniqueChunks = new HashSet<string>(StringComparer.Ordinal);
        var paletteNames = new HashSet<string>(StringComparer.Ordinal);
        using var compressed = new MemoryStream();

        while (offset < input.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++chunkCount > limits.MaxChunkCount)
                throw new ImageLimitException("PNG chunk count exceeds the configured limit.");
            if (input.Length - offset < 12)
                throw Invalid("PNG chunk header or CRC is truncated.");
            var unsignedLength = BinaryPrimitives.ReadUInt32BigEndian(input.Slice(offset, 4));
            if (unsignedLength > int.MaxValue)
                throw Invalid("PNG chunk length exceeds the PNG 31-bit limit.");
            if (unsignedLength > limits.MaxChunkBytes)
                throw new ImageLimitException("PNG chunk length exceeds the configured limit.");
            var length = (int)unsignedLength;
            if (length > input.Length - offset - 12)
                throw Invalid("PNG chunk data is truncated.");
            var type = input.Slice(offset + 4, 4);
            for (var i = 0; i < 4; i++)
                if (type[i] is not (>= (byte)'A' and <= (byte)'Z') and not (>= (byte)'a' and <= (byte)'z'))
                    throw Invalid("PNG chunk type must contain four ASCII letters.");
            if ((type[2] & 32) != 0)
                throw Invalid("PNG chunk type has an invalid reserved bit.");
            var name = Encoding.ASCII.GetString(type);
            var payload = input.Slice(offset + 8, length);
            var expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(input.Slice(offset + 8 + length, 4));
            if (ComputeCrc(input.Slice(offset + 4, length + 4), cancellationToken) != expectedCrc)
                throw Invalid($"PNG {name} CRC is invalid.");
            offset += length + 12;
            if (!sawHeader && name != "IHDR")
                throw Invalid("PNG IHDR must be the first chunk.");
            if (sawData && name != "IDAT")
                endedData = true;

            switch (name)
            {
                case "IHDR":
                    if (sawHeader || length != 13)
                        throw Invalid("PNG must have one 13-byte IHDR chunk.");
                    var imageWidth = BinaryPrimitives.ReadUInt32BigEndian(payload[..4]);
                    var imageHeight = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(4, 4));
                    if (imageWidth == 0 || imageHeight == 0 || imageWidth > int.MaxValue || imageHeight > int.MaxValue)
                        throw Invalid("PNG dimensions must be positive 31-bit integers.");
                    var depth = payload[8];
                    color = payload[9];
                    if (!(color switch
                    {
                        0 => depth is 1 or 2 or 4 or 8 or 16,
                        2 or 4 or 6 => depth is 8 or 16,
                        3 => depth is 1 or 2 or 4 or 8,
                        _ => false
                    }))
                        throw Invalid("PNG color type and bit depth are not a valid combination.");
                    if (payload[10] != 0 || payload[11] != 0 || payload[12] > 1)
                        throw Invalid("PNG compression, filter or interlace method is invalid.");
                    if (color == 3 || depth != 8 || payload[12] != 0)
                        throw Unsupported("palette, sub-8/16-bit or Adam7 images");
                    outputLength = limits.ValidateDimensions(imageWidth, imageHeight);
                    width = (int)imageWidth;
                    height = (int)imageHeight;
                    channels = color switch { 0 => 1, 2 => 3, 4 => 2, _ => 4 };
                    var scanlineBytes = checked(((long)width * channels + 1) * height);
                    if (scanlineBytes > limits.MaxDecodedBytes || scanlineBytes > Array.MaxLength)
                        throw new ImageLimitException("PNG decompressed scanline bytes exceed the configured limit.");
                    scanlineLength = checked(width * channels);
                    sawHeader = true;
                    break;

                case "PLTE":
                    Single(uniqueChunks, name);
                    BeforeData(sawData, name);
                    if (color is 0 or 4 || length is < 3 or > 768 || length % 3 != 0)
                        throw Invalid("PNG suggested PLTE must contain 1–256 RGB entries and is allowed only for RGB/RGBA input.");
                    if (transparent is not null || uniqueChunks.Contains("bKGD") || uniqueChunks.Contains("hIST"))
                        throw Invalid("PNG PLTE must precede tRNS, bKGD and hIST.");
                    paletteEntries = length / 3;
                    break;

                case "IDAT":
                    if (endedData)
                        throw Invalid("PNG IDAT chunks must be consecutive.");
                    sawData = true;
                    compressed.Write(payload);
                    break;

                case "IEND":
                    if (!sawData || length != 0)
                        throw Invalid("PNG IEND must be empty and follow IDAT.");
                    if (offset != input.Length)
                        throw Invalid("PNG has trailing data after IEND.");
                    sawEnd = true;
                    break;

                case "tRNS":
                    Single(uniqueChunks, name);
                    BeforeData(sawData, name);
                    if (color is 4 or 6 || length != (color == 0 ? 2 : 6))
                        throw Invalid("PNG tRNS length or color type is invalid; alpha images cannot contain tRNS.");
                    // PNG 3 section 11.3.1.1 requires masking unused high bits before tRNS matching.
                    transparent = color == 0 ? [payload[1]] : [payload[1], payload[3], payload[5]];
                    break;

                case "sRGB":
                case "gAMA":
                case "cHRM":
                case "sBIT":
                    Single(uniqueChunks, name);
                    BeforeData(sawData || paletteEntries != 0, name);
                    ValidateColorMetadata(name, payload, channels);
                    break;

                case "acTL":
                case "fcTL":
                case "fdAT":
                    throw Unsupported("APNG animation");
                case "iCCP":
                case "cICP":
                case "mDCV":
                case "cLLI":
                    throw Unsupported($"{name} color management or HDR metadata");
                case "eXIf":
                    throw Unsupported("eXIf orientation or metadata");

                default:
                    if ((type[0] & 32) == 0)
                        throw Unsupported($"unknown critical chunk {name}");
                    ValidateAncillary(name, payload, sawData, color, paletteEntries, uniqueChunks, paletteNames);
                    break;
            }
            if (sawEnd)
                break;
        }
        if (!sawEnd)
            throw Invalid("PNG is missing IEND or image data.");
        return DecodePixels(width, height, channels, color, outputLength, scanlineLength,
            compressed.GetBuffer().AsMemory(0, (int)compressed.Length), transparent, cancellationToken);
    }

    /// <summary>Encodes RGBA8 with filter 0, an explicit sRGB chunk, and bounded IDAT chunks.</summary>
    public static byte[] Encode(RgbaImage image, CompressionLevel compressionLevel = CompressionLevel.Optimal,
        ImageLimits? limits = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        cancellationToken.ThrowIfCancellationRequested();
        limits ??= ImageLimits.Default;
        limits.ValidateDimensions(image.Width, image.Height);
        if (!Enum.IsDefined(compressionLevel))
            throw new ArgumentOutOfRangeException(nameof(compressionLevel));
        var stride = checked(image.Width * 4);
        if (((long)stride + 1) * image.Height > limits.MaxDecodedBytes)
            throw new ImageLimitException("PNG decompressed scanline bytes exceed the configured limit.");
        if (limits.MaxChunkBytes < 13 || limits.MaxChunkCount < 4)
            throw new ImageLimitException("PNG encoding requires a 13-byte IHDR and at least four chunks.");
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, compressionLevel, leaveOpen: true))
        {
            for (var y = 0; y < image.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                zlib.WriteByte(0);
                zlib.Write(image.Pixels.Span.Slice(y * stride, stride));
                if (compressed.Length > limits.MaxInputBytes)
                    throw new ImageLimitException("Encoded PNG bytes exceed the configured limit.");
            }
        }
        var chunkSize = Math.Min(64 * 1024, limits.MaxChunkBytes);
        var idatCount = (compressed.Length + chunkSize - 1) / chunkSize;
        if (idatCount + 3 > limits.MaxChunkCount)
            throw new ImageLimitException("Encoded PNG chunk count exceeds the configured limit.");
        var outputLength = checked(8 + 25 + 13 + compressed.Length + idatCount * 12 + 12);
        if (outputLength > limits.MaxInputBytes || outputLength > Array.MaxLength)
            throw new ImageLimitException("Encoded PNG bytes exceed the configured limit.");
        using var output = new MemoryStream((int)outputLength);
        output.Write(Signature);
        Span<byte> header = stackalloc byte[13];
        header.Clear();
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)image.Width);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)image.Height);
        header[8] = 8;
        header[9] = 6;
        WriteChunk(output, "IHDR"u8, header, cancellationToken);
        WriteChunk(output, "sRGB"u8, [0], cancellationToken);
        var data = compressed.GetBuffer().AsSpan(0, (int)compressed.Length);
        while (!data.IsEmpty)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(data.Length, chunkSize);
            WriteChunk(output, "IDAT"u8, data[..count], cancellationToken);
            data = data[count..];
        }
        WriteChunk(output, "IEND"u8, [], cancellationToken);
        return output.ToArray();
    }

    private static RgbaImage DecodePixels(int width, int height, int channels, int color, int outputLength,
        int stride, ReadOnlyMemory<byte> compressed, byte[]? transparent, CancellationToken cancellationToken)
    {
        ValidateZlibHeader(compressed.Span);
        var output = new byte[outputLength];
        var previous = new byte[stride];
        var current = new byte[stride];
        // Single-byte compressed reads expose exact member consumption. EOF throws so ZLibStream
        // cannot silently accept an unfinished stream; a valid member finishes before requesting EOF.
        using var input = new ExactZlibInput(compressed, cancellationToken);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        uint adlerA = 1;
        uint adlerB = 0;
        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var filter = zlib.ReadByte();
            if (filter is < 0 or > 4)
                throw Invalid(filter < 0 ? "PNG decompressed scanlines are truncated." : "PNG scanline filter is invalid.");
            AddAdler((byte)filter, ref adlerA, ref adlerB);
            ReadScanline(zlib, current, cancellationToken);
            for (var i = 0; i < stride; i++)
            {
                if ((i & 4095) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                AddAdler(current[i], ref adlerA, ref adlerB);
                var left = i >= channels ? current[i - channels] : 0;
                var above = previous[i];
                var upperLeft = i >= channels ? previous[i - channels] : 0;
                var prediction = filter switch
                {
                    0 => 0,
                    1 => left,
                    2 => above,
                    3 => (left + above) / 2,
                    _ => Paeth(left, above, upperLeft)
                };
                current[i] = unchecked((byte)(current[i] + prediction));
            }
            for (var x = 0; x < width; x++)
            {
                if ((x & 4095) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                var from = x * channels;
                var to = (y * width + x) * 4;
                if (color is 0 or 4)
                {
                    output[to] = output[to + 1] = output[to + 2] = current[from];
                    output[to + 3] = color == 4 ? current[from + 1]
                        : transparent is not null && current[from] == transparent[0] ? (byte)0 : (byte)255;
                }
                else
                {
                    current.AsSpan(from, 3).CopyTo(output.AsSpan(to, 3));
                    output[to + 3] = color == 6 ? current[from + 3]
                        : transparent is not null && current[from] == transparent[0]
                            && current[from + 1] == transparent[1] && current[from + 2] == transparent[2] ? (byte)0 : (byte)255;
                }
            }
            (current, previous) = (previous, current);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (zlib.ReadByte() != -1)
            throw Invalid("PNG decompressed image data exceeds the required scanline length.");
        if (input.BytesRead != compressed.Length)
            throw Invalid("PNG IDAT contains trailing bytes or multiple zlib streams.");
        var expectedAdler = BinaryPrimitives.ReadUInt32BigEndian(compressed.Span[^4..]);
        if ((adlerB << 16 | adlerA) != expectedAdler)
            throw Invalid("PNG zlib checksum is missing or invalid.");
        return new RgbaImage(width, height, output);
    }

    private static void ReadScanline(Stream stream, Span<byte> destination, CancellationToken cancellationToken)
    {
        while (!destination.IsEmpty)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(destination);
            if (read == 0)
                throw Invalid("PNG decompressed scanlines are truncated.");
            destination = destination[read..];
        }
    }

    private static void AddAdler(byte value, ref uint a, ref uint b)
    {
        a = (a + value) % 65521;
        b = (b + a) % 65521;
    }

    private static int Paeth(int left, int above, int upperLeft)
    {
        var p = left + above - upperLeft;
        var dl = Math.Abs(p - left);
        var da = Math.Abs(p - above);
        var du = Math.Abs(p - upperLeft);
        return dl <= da && dl <= du ? left : da <= du ? above : upperLeft;
    }

    private static void ValidateColorMetadata(string name, ReadOnlySpan<byte> payload, int channels)
    {
        switch (name)
        {
            case "sRGB":
                if (payload.Length != 1 || payload[0] > 3)
                    throw Invalid("PNG sRGB rendering intent is invalid.");
                break;
            case "gAMA":
                if (payload.Length != 4 || BinaryPrimitives.ReadUInt32BigEndian(payload) is 0 or > int.MaxValue)
                    throw Invalid("PNG gAMA must contain one positive four-byte gamma value.");
                if (BinaryPrimitives.ReadUInt32BigEndian(payload) != 45455)
                    throw Unsupported("gAMA differing from the sRGB value 45455");
                break;
            case "cHRM":
                if (payload.Length != 32)
                    throw Invalid("PNG cHRM must contain eight four-byte chromaticity values.");
                ReadOnlySpan<uint> srgb = [31270, 32900, 64000, 33000, 30000, 60000, 15000, 6000];
                for (var i = 0; i < srgb.Length; i++)
                {
                    if (BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(i * 4, 4)) > int.MaxValue)
                        throw Invalid("PNG cHRM chromaticity is outside the PNG integer range.");
                    if (BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(i * 4, 4)) != srgb[i])
                        throw Unsupported("cHRM differing from the fixed sRGB chromaticities");
                }
                break;
            case "sBIT":
                if (payload.Length != channels || payload.Contains((byte)0))
                    throw Invalid("PNG sBIT length or significant-bit count is invalid.");
                foreach (var value in payload)
                    if (value > 8)
                        throw Invalid("PNG sBIT significant bits exceed the image bit depth.");
                break;
        }
    }

    private static void ValidateAncillary(string name, ReadOnlySpan<byte> payload, bool sawData, int color,
        int paletteEntries, HashSet<string> uniqueChunks, HashSet<string> paletteNames)
    {
        switch (name)
        {
            case "tEXt":
                var textOffset = KeywordEnd(payload) + 1;
                if (payload[textOffset..].Contains((byte)0))
                    throw Invalid("PNG tEXt text cannot contain a null byte.");
                break;
            case "zTXt":
                var compressedOffset = KeywordEnd(payload) + 1;
                if (payload.Length <= compressedOffset || payload[compressedOffset] != 0)
                    throw Invalid("PNG zTXt compression method is invalid.");
                ValidateZlibHeader(payload[(compressedOffset + 1)..]);
                break;
            case "iTXt":
                ValidateInternationalText(payload);
                break;
            case "pHYs":
                Single(uniqueChunks, name);
                BeforeData(sawData, name);
                if (payload.Length != 9 || payload[8] > 1
                    || BinaryPrimitives.ReadUInt32BigEndian(payload) > int.MaxValue
                    || BinaryPrimitives.ReadUInt32BigEndian(payload[4..]) > int.MaxValue)
                    throw Invalid("PNG pHYs length or unit is invalid.");
                break;
            case "tIME":
                Single(uniqueChunks, name);
                if (payload.Length != 7 || payload[2] is < 1 or > 12 || payload[3] is < 1 or > 31
                    || payload[4] > 23 || payload[5] > 59 || payload[6] > 60)
                    throw Invalid("PNG tIME timestamp is invalid.");
                break;
            case "bKGD":
                Single(uniqueChunks, name);
                BeforeData(sawData, name);
                if (payload.Length != (color is 0 or 4 ? 2 : 6))
                    throw Invalid("PNG bKGD length does not match the color type.");
                break;
            case "hIST":
                Single(uniqueChunks, name);
                BeforeData(sawData, name);
                if (paletteEntries == 0 || payload.Length != paletteEntries * 2)
                    throw Invalid("PNG hIST requires one frequency for every PLTE entry.");
                break;
            case "sPLT":
                BeforeData(sawData, name);
                var keywordLength = KeywordEnd(payload);
                var paletteName = Encoding.Latin1.GetString(payload[..keywordLength]);
                if (!paletteNames.Add(paletteName))
                    throw Invalid("PNG sPLT palette names must be distinct.");
                var start = keywordLength + 1;
                if (payload.Length <= start || payload[start] is not (8 or 16))
                    throw Invalid("PNG sPLT sample depth is invalid.");
                var entrySize = payload[start] == 8 ? 6 : 10;
                if ((payload.Length - start - 1) % entrySize != 0)
                    throw Invalid("PNG sPLT palette entries are truncated.");
                var lastFrequency = ushort.MaxValue;
                for (var i = start + 1; i < payload.Length; i += entrySize)
                {
                    var frequency = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(i + entrySize - 2, 2));
                    if (frequency > lastFrequency)
                        throw Invalid("PNG sPLT frequencies must be in decreasing order.");
                    lastFrequency = frequency;
                }
                break;
        }
    }

    private static int KeywordEnd(ReadOnlySpan<byte> payload)
    {
        var end = payload.IndexOf((byte)0);
        if (end is < 1 or > 79 || payload[0] == 32 || payload[end - 1] == 32)
            throw Invalid("PNG text or palette keyword must contain 1–79 valid Latin-1 bytes.");
        for (var i = 0; i < end; i++)
            if (!(payload[i] is >= 32 and <= 126 or >= 161) || (i != 0 && payload[i] == 32 && payload[i - 1] == 32))
                throw Invalid("PNG keyword contains an invalid character or consecutive spaces.");
        return end;
    }

    private static void ValidateInternationalText(ReadOnlySpan<byte> payload)
    {
        var start = KeywordEnd(payload) + 1;
        if (payload.Length - start < 4 || payload[start] > 1 || (payload[start] == 1 && payload[start + 1] != 0))
            throw Invalid("PNG iTXt compression fields are invalid.");
        var compressed = payload[start] == 1;
        start += 2;
        var languageEnd = payload[start..].IndexOf((byte)0);
        if (languageEnd < 0)
            throw Invalid("PNG iTXt language tag is unterminated.");
        var language = payload.Slice(start, languageEnd);
        var subtagLength = 0;
        var primary = true;
        foreach (var value in language)
        {
            if (value == '-')
            {
                if (subtagLength == 0)
                    throw Invalid("PNG iTXt language tag has an empty subtag.");
                subtagLength = 0;
                primary = false;
            }
            else if (value is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9')
            {
                if (primary && value is >= (byte)'0' and <= (byte)'9')
                    throw Invalid("PNG iTXt primary language subtag must contain letters.");
                if (++subtagLength > 8)
                    throw Invalid("PNG iTXt language tag subtag is too long.");
            }
            else
                throw Invalid("PNG iTXt language tag must contain ASCII letters, digits and hyphens.");
        }
        if (!language.IsEmpty && subtagLength == 0)
            throw Invalid("PNG iTXt language tag has an empty subtag.");
        start += languageEnd + 1;
        var translatedEnd = payload[start..].IndexOf((byte)0);
        if (translatedEnd < 0)
            throw Invalid("PNG iTXt translated keyword is unterminated.");
        ValidateUtf8(payload.Slice(start, translatedEnd));
        start += translatedEnd + 1;
        if (compressed)
            ValidateZlibHeader(payload[start..]);
        else
            ValidateUtf8(payload[start..]);
    }

    private static void ValidateUtf8(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Contains((byte)0))
            throw Invalid("PNG iTXt text cannot contain a null byte.");
        try
        {
            StrictUtf8.GetCharCount(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("PNG iTXt text is not valid UTF-8.", exception);
        }
    }

    private static void ValidateZlibHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < 6 || (data[0] & 15) != 8 || (data[0] >> 4) > 7
            || ((data[0] << 8) + data[1]) % 31 != 0 || (data[1] & 32) != 0)
            throw Invalid("PNG zlib header is truncated or invalid, or requests a preset dictionary.");
    }

    private static void Single(HashSet<string> seen, string name)
    {
        if (!seen.Add(name))
            throw Invalid($"PNG {name} chunk must not be repeated.");
    }

    private static void BeforeData(bool sawData, string name)
    {
        if (sawData)
            throw Invalid($"PNG {name} chunk appears after its permitted position.");
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data, CancellationToken cancellationToken)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(number, (uint)data.Length);
        output.Write(number);
        output.Write(type);
        output.Write(data);
        var crc = UpdateCrc(uint.MaxValue, type, cancellationToken);
        crc = UpdateCrc(crc, data, cancellationToken);
        BinaryPrimitives.WriteUInt32BigEndian(number, ~crc);
        output.Write(number);
    }

    private static uint ComputeCrc(ReadOnlySpan<byte> data, CancellationToken cancellationToken) =>
        ~UpdateCrc(uint.MaxValue, data, cancellationToken);

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> data, CancellationToken cancellationToken)
    {
        for (var i = 0; i < data.Length; i++)
        {
            if ((i & 65535) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            crc = CrcTable[(crc ^ data[i]) & 255] ^ (crc >> 8);
        }
        return crc;
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
                value = (value & 1) == 0 ? value >> 1 : 0xedb88320U ^ (value >> 1);
            table[i] = value;
        }
        return table;
    }

    private static InvalidDataException Invalid(string message) => new(message);
    private static NotSupportedException Unsupported(string feature) => new(
        $"PNG conversion does not support {feature}. Register the original or pre-generated variants as pass-through assets.");

    private sealed class ExactZlibInput(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) : Stream
    {
        public int BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (buffer.IsEmpty)
                return 0;
            if (BytesRead == data.Length)
                throw Invalid("PNG zlib stream is truncated before its end marker or checksum.");
            buffer[0] = data.Span[BytesRead++];
            return 1;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
