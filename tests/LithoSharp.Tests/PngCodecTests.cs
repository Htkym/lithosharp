using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using LithoSharp.Images;

namespace LithoSharp.Tests;

public sealed class PngCodecTests
{
    // Independent fixtures generated with Node's node:zlib, following W3C PNG 3 sections 9 and 11.
    // Each 3x5 image uses filter types 0, 1, 2, 3, and 4 on consecutive scanlines.
    private static readonly string[] FilterFixtures =
    [
        "iVBORw0KGgoAAAANSUhEUgAAAAMAAAAFCAAAAAClGgl+AAAAHElEQVR4nGMQtEln9NPWZrK1tWVuNjFhsdW2BQAkEgOqfQRiDwAAAABJRU5ErkJggg==",
        "iVBORw0KGgoAAAANSUhEUgAAAAMAAAAFCAIAAAAPE8H1AAAALUlEQVR4nGMQTFhv030rfRsro9/cN9pgwGQLA8zNq4JMwIDF1tZWW1vbVlsbAIDEDW4dh4zdAAAAAElFTkSuQmCC",
        "iVBORw0KGgoAAAANSUhEUgAAAAMAAAAFCAQAAAAqeJ4pAAAAKElEQVR4nGMQTLDpTt/G6DdXW1tbm8kWDJibV5mYmJiw2Npqa9tqAwCkGQianOW89QAAAABJRU5ErkJggg==",
        "iVBORw0KGgoAAAANSUhEUgAAAAMAAAAFCAYAAACAcVaiAAAAMUlEQVR4nGMQTFj/z6b7lmb6NtYQRr+5b6y1oYDJFgkwN68KqjSBAhaQCEiJrba2LQCWBhG3flTN8QAAAABJRU5ErkJggg=="
    ];

    private static readonly byte[][] FilterPixels =
    [
        [17,17,17,255,60,60,60,255,103,103,103,255,78,78,78,255,121,121,121,255,164,164,164,255,139,139,139,255,182,182,182,255,225,225,225,255,200,200,200,255,243,243,243,255,30,30,30,255,5,5,5,255,48,48,48,255,91,91,91,255],
        [17,96,175,255,60,139,218,255,103,182,5,255,78,157,236,255,121,200,23,255,164,243,66,255,139,218,41,255,182,5,84,255,225,48,127,255,200,23,102,255,243,66,145,255,30,109,188,255,5,84,163,255,48,127,206,255,91,170,249,255],
        [17,17,17,96,60,60,60,139,103,103,103,182,78,78,78,157,121,121,121,200,164,164,164,243,139,139,139,218,182,182,182,5,225,225,225,48,200,200,200,23,243,243,243,66,30,30,30,109,5,5,5,84,48,48,48,127,91,91,91,170],
        [17,96,175,254,60,139,218,41,103,182,5,84,78,157,236,59,121,200,23,102,164,243,66,145,139,218,41,120,182,5,84,163,225,48,127,206,200,23,102,181,243,66,145,224,30,109,188,11,5,84,163,242,48,127,206,29,91,170,249,72]
    ];

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task IndependentFixturesDecodeEverySupportedColorAndFilter(int fixture)
    {
        var bytes = Convert.FromBase64String(FilterFixtures[fixture]);
        var decoded = PngCodec.Decode(bytes);
        await Assert.That(decoded.Width).IsEqualTo(3);
        await Assert.That(decoded.Height).IsEqualTo(5);
        await BytesEqual(decoded.Pixels.ToArray(), FilterPixels[fixture]);

        using var stream = new MemoryStream(bytes);
        var streamed = PngCodec.Decode(stream);
        await BytesEqual(streamed.Pixels.ToArray(), FilterPixels[fixture]);
        await Assert.That(stream.CanRead).IsTrue();
    }

    [Test]
    public async Task FirstRowPredictorsAndPaethUpperLeftUseTheSpecifiedBoundaries()
    {
        foreach (var row in new byte[][] { [1, 10, 10, 10], [2, 10, 20, 30], [3, 10, 15, 20], [4, 10, 10, 10] })
        {
            var png = Png(Header(3, 1, 0), new("IDAT", Compress(row)), new("IEND", []));
            await BytesEqual(PngCodec.Decode(png).Pixels.ToArray(), [10, 10, 10, 255, 20, 20, 20, 255, 30, 30, 30, 255]);
        }
        var paeth = Png(Header(2, 2, 0), new("IDAT", Compress([0, 100, 150, 4, 216, 20])), new("IEND", []));
        await BytesEqual(PngCodec.Decode(paeth).Pixels.ToArray(), [100, 100, 100, 255, 150, 150, 150, 255, 60, 60, 60, 255, 120, 120, 120, 255]);
    }

    [Test]
    public async Task MultipleIdatSuggestedPaletteAndTransparencyUseKnownPixels()
    {
        var compressed = Compress([0, 12, 34, 56, 90, 80, 70]);
        var bytes = Png(Header(2, 1, 2), new("PLTE", [12, 34, 56, 90, 80, 70]),
            new("tRNS", [0x12, 12, 0x34, 34, 0x56, 56]), new("IDAT", compressed[..1]),
            new("IDAT", []), new("IDAT", compressed[1..]), new("IEND", []));
        await BytesEqual(PngCodec.Decode(bytes).Pixels.ToArray(), [0, 0, 0, 0, 90, 80, 70, 255]);

        var grey = Png(Header(2, 1, 0), new("tRNS", [0x90, 17]),
            new("IDAT", Compress([0, 17, 42])), new("IEND", []));
        await BytesEqual(PngCodec.Decode(grey).Pixels.ToArray(), [0, 0, 0, 0, 42, 42, 42, 255]);

        var rgba = Png(Header(1, 1, 6), new("PLTE", [255, 0, 0]),
            new("IDAT", Compress([0, 1, 2, 3, 4])), new("IEND", []));
        await BytesEqual(PngCodec.Decode(rgba).Pixels.ToArray(), [1, 2, 3, 4]);
    }

    [Test]
    public async Task SrgbMetadataAndStructurallyValidTextAreAccepted()
    {
        var chromaticities = new uint[] { 31270, 32900, 64000, 33000, 30000, 60000, 15000, 6000 };
        var chromaticityBytes = chromaticities.SelectMany(UInt32).ToArray();
        var bytes = Png(Header(), new("sRGB", [0]), new("gAMA", UInt32(45455)),
            new("cHRM", chromaticityBytes), new("tEXt", "Comment\0fixture"u8.ToArray()),
            new("zTXt", [.. "Comment\0"u8, 0, .. Compress("text"u8.ToArray())]),
            new("iTXt", "Comment\0\0\0en\0Comment\0text"u8.ToArray()),
            new("IDAT", Compress([0, 11, 22, 33, 255])), new("IEND", []));
        await BytesEqual(PngCodec.Decode(bytes).Pixels.ToArray(), [11, 22, 33, 255]);
    }

    [Test]
    public async Task UnsupportedFeaturesReportConversionLimits()
    {
        var cases = new List<byte[]>
        {
            Png(Header(color: 3), new("PLTE", [0,0,0]), new("IDAT", Compress([0,0])), new("IEND", [])),
            Png(Header(depth: 16), new("IDAT", Compress([0,0,0,0,0,0,0,0,0])), new("IEND", [])),
            Png(Header(interlace: 1), new("IDAT", Compress([0,1,2,3,255])), new("IEND", []))
        };
        foreach (var chunk in new Chunk[]
        {
            new("acTL", [0,0,0,1,0,0,0,0]), new("fcTL", new byte[26]), new("fdAT", [0,0,0,0]),
            new("iCCP", [.. "Profile\0"u8, 0, .. Compress([0])]), new("cICP", [1,13,0,1]),
            new("mDCV", new byte[24]), new("cLLI", new byte[8]), new("eXIf", [73,73,42,0,8,0,0,0]),
            new("gAMA", UInt32(100000)), new("cHRM", new byte[32]), new("ABCD", [])
        })
            cases.Add(Png(Header(), chunk, new("IDAT", Compress([0, 1, 2, 3, 255])), new("IEND", [])));

        foreach (var bytes in cases)
            await Assert.That(() => PngCodec.Decode(bytes)).Throws<NotSupportedException>();
    }

    [Test]
    public async Task InvalidStructureCrcAndChunkOrderAreRejected()
    {
        var idat = new Chunk("IDAT", Compress([0, 1, 2, 3, 255]));
        var valid = Png(Header(), idat, new("IEND", []));
        var badCrc = valid.ToArray();
        badCrc[29] ^= 1;
        var badSignature = valid.ToArray();
        badSignature[0] = 0;
        var cases = new List<byte[]>
        {
            badCrc, badSignature, valid[..^1], valid[..^12], valid.Concat(new byte[] { 0 }).ToArray(),
            Png(Header(), new("IEND", [])), Png(idat, Header(), new("IEND", [])),
            Png(Header(), Header(), idat, new("IEND", [])),
            Png(Header(), idat, new("tEXt", "Key\0value"u8.ToArray()), idat, new("IEND", [])),
            Png(Header(), idat, new("IEND", [0])),
            Png(Header(), new("ab1D", []), idat, new("IEND", [])),
            Png(Header(0), idat, new("IEND", [])),
            Png(Header(color: 4), new("tRNS", [0,0]), new("IDAT", Compress([0,1,255])), new("IEND", [])),
            Png(Header(), new("tRNS", [0,0,0,0,0,0]), idat, new("IEND", [])),
            Png(Header(color: 0), new("PLTE", [1,2,3]), new("IDAT", Compress([0,1])), new("IEND", [])),
            Png(Header(color: 2), new("PLTE", [1,2]), new("IDAT", Compress([0,1,2,3])), new("IEND", [])),
            Png(Header(), new("sRGB", [4]), idat, new("IEND", [])),
            Png(Header(), new("sRGB", [0]), new("sRGB", [0]), idat, new("IEND", [])),
            Png(Header(), idat, new("gAMA", UInt32(45455)), new("IEND", []))
        };
        foreach (var bytes in cases)
            await Assert.That(() => PngCodec.Decode(bytes)).Throws<InvalidDataException>();
    }

    [Test]
    public async Task MalformedTextMetadataIsRejectedWithoutExpandingItsPayload()
    {
        foreach (var chunk in new Chunk[]
        {
            new("tEXt", "missing separator"u8.ToArray()), new("tEXt", [0,65]),
            new("tEXt", "Key\0bad\0text"u8.ToArray()),
            new("zTXt", "Key\0"u8.ToArray()), new("zTXt", [.. "Key\0"u8, 1, 1]),
            new("iTXt", "Key\0\0\0en"u8.ToArray()),
            new("iTXt", [.. "Key\0"u8, 0, 0, .. "123\0\0text"u8]),
            new("iTXt", [.. "Key\0"u8, 2, 0, 0, 0]),
            new("iTXt", [.. "Key\0"u8, 0, 0, 0, 0, 0xff])
        })
        {
            var bytes = Png(Header(), chunk, new("IDAT", Compress([0, 1, 2, 3, 255])), new("IEND", []));
            await Assert.That(() => PngCodec.Decode(bytes)).Throws<InvalidDataException>();
        }
    }

    [Test]
    public async Task ZlibCorruptionTruncationAndWrongScanlineAmountsAreRejected()
    {
        var compressed = Compress([0, 1, 2, 3, 255]);
        var corrupt = compressed.ToArray();
        corrupt[^1] ^= 1;
        // Stored deflate block with BFINAL cleared, preserving all pixels and the correct Adler-32.
        byte[] unfinished = [0x78, 0x01, 0x00, 5, 0, 0xfa, 0xff, 0, 1, 2, 3, 255, 0x01, 0x14, 0x01, 0x06];
        foreach (var data in new byte[][]
        {
            corrupt, compressed[..^1], Compress([0,1,2,3]), Compress([0,1,2,3,255,0]),
            Compress([5,1,2,3,255]), [.. compressed, 0], [.. compressed, .. compressed], unfinished
        })
        {
            var bytes = Png(Header(), new("IDAT", data), new("IEND", []));
            await Assert.That(() => PngCodec.Decode(bytes)).Throws<InvalidDataException>();
        }
    }

    [Test]
    public async Task LimitsStopBeforeLargeAllocationAndCancellationRemainsDistinct()
    {
        var bytes = Convert.FromBase64String(FilterFixtures[3]);
        foreach (var limits in new ImageLimits[]
        {
            new() { MaxInputBytes = bytes.Length - 1 }, new() { MaxChunkBytes = 12 },
            new() { MaxChunkCount = 2 }, new() { MaxWidth = 2 }, new() { MaxHeight = 4 },
            new() { MaxPixels = 14 }, new() { MaxDecodedBytes = 59 }, new() { MaxDecodedBytes = 64 }
        })
            await Assert.That(() => PngCodec.Decode(bytes, limits)).Throws<ImageLimitException>();

        var huge = Png(Header(int.MaxValue, int.MaxValue), new("IDAT", []), new("IEND", []));
        await Assert.That(() => PngCodec.Decode(huge)).Throws<ImageLimitException>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(() => PngCodec.Decode(bytes, cancellationToken: cancellation.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(() => PngCodec.Encode(RgbaImage.Create(1, 1, 1, 2, 3), cancellationToken: cancellation.Token))
            .Throws<OperationCanceledException>();
    }

    [Test]
    public async Task UnseekablePartialReadsRespectInputLimitAndMidstreamCancellation()
    {
        var bytes = Convert.FromBase64String(FilterFixtures[0]);
        using var partial = new PartialReadStream(bytes);
        await BytesEqual(PngCodec.Decode(partial).Pixels.ToArray(), FilterPixels[0]);
        using var limited = new PartialReadStream(bytes);
        await Assert.That(() => PngCodec.Decode(limited, new ImageLimits { MaxInputBytes = bytes.Length - 1 }))
            .Throws<ImageLimitException>();

        using var cancellation = new CancellationTokenSource();
        using var cancelled = new PartialReadStream(bytes, () => cancellation.Cancel());
        await Assert.That(() => PngCodec.Decode(cancelled, cancellationToken: cancellation.Token))
            .Throws<OperationCanceledException>();
    }

    [Test]
    public async Task EncoderProducesKnownRgbaScanlinesAndValidChunkChecksums()
    {
        var image = new RgbaImage(2, 2, [255, 0, 0, 255, 0, 255, 0, 128, 0, 0, 255, 255, 90, 80, 70, 0]);
        var bytes = PngCodec.Encode(image, CompressionLevel.NoCompression);
        await BytesEqual(bytes[..8], [137, 80, 78, 71, 13, 10, 26, 10]);
        var chunks = ReadChunks(bytes);
        await Assert.That(chunks.First().Type).IsEqualTo("IHDR");
        await Assert.That(chunks.Last().Type).IsEqualTo("IEND");
        await BytesEqual(chunks.Single(c => c.Type == "IHDR").Data, [0, 0, 0, 2, 0, 0, 0, 2, 8, 6, 0, 0, 0]);
        await Assert.That(chunks.All(c => c.Type is "IHDR" or "sRGB" or "IDAT" or "IEND")).IsTrue();
        var compressed = chunks.Where(c => c.Type == "IDAT").SelectMany(c => c.Data).ToArray();
        using var input = new MemoryStream(compressed);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        await BytesEqual(output.ToArray(),
        [
            0,255,0,0,255,0,255,0,128,
            0,0,0,255,255,0,0,0,0
        ]);
    }

    [Test]
    public async Task BilinearUsesPixelCentersPremultipliedAlphaAndHalfUpDimensions()
    {
        var image = new RgbaImage(2, 1, [255, 0, 0, 255, 0, 0, 255, 0]);
        var resized = image.Resize(3);
        await Assert.That(resized.Width).IsEqualTo(3);
        await Assert.That(resized.Height).IsEqualTo(2);
        await BytesEqual(resized.Pixels.ToArray(),
        [
            255,0,0,255,255,0,0,128,0,0,0,0,
            255,0,0,255,255,0,0,128,0,0,0,0
        ]);

        var square = new RgbaImage(2, 2, [0, 0, 0, 255, 100, 0, 0, 255, 0, 100, 0, 255, 100, 100, 0, 255]);
        await BytesEqual(square.Resize(1).Pixels.ToArray(), [50, 50, 0, 255]);
        var enlarged = square.Resize(3);
        await BytesEqual(enlarged.Pixels.ToArray(),
        [
            0,0,0,255,50,0,0,255,100,0,0,255,
            0,50,0,255,50,50,0,255,100,50,0,255,
            0,100,0,255,50,100,0,255,100,100,0,255
        ]);
        await Assert.That(RgbaImage.Create(5, 1, 1, 2, 3).Resize(1).Height).IsEqualTo(1);
        await BytesEqual(RgbaImage.Create(1, 1, 9, 8, 7, 6).Resize(4).Pixels.ToArray(),
            Enumerable.Range(0, 16).SelectMany(_ => new byte[] { 9, 8, 7, 6 }).ToArray());
    }

    [Test]
    public async Task CanvasPlacementClipsAndCompositesSourceOver()
    {
        var canvas = RgbaImage.Create(1, 1, 0, 0, 255, 128);
        canvas.Composite(RgbaImage.Create(1, 1, 255, 0, 0, 128), 0, 0);
        await BytesEqual(canvas.Pixels.ToArray(), [170, 0, 85, 192]);

        var clipped = RgbaImage.Create(2, 1, 0, 0, 255);
        clipped.Composite(new RgbaImage(2, 1, [0, 255, 0, 255, 255, 0, 0, 255]), -1, 0);
        clipped.Composite(RgbaImage.Create(1, 1, 1, 2, 3), int.MinValue, int.MaxValue);
        await BytesEqual(clipped.Pixels.ToArray(), [255, 0, 0, 255, 0, 0, 255, 255]);
        clipped.FillRectangle(-1, 0, 2, 1, 200, 100, 50, 0);
        await BytesEqual(clipped.Pixels.ToArray(), [0, 0, 0, 0, 0, 0, 255, 255]);

        var overlap = new RgbaImage(3, 1, [255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255]);
        overlap.Composite(overlap, 1, 0);
        await BytesEqual(overlap.Pixels.ToArray(), [255, 0, 0, 255, 255, 0, 0, 255, 0, 255, 0, 255]);
    }

    [Test]
    public async Task PixelOperationsValidateDimensionsLimitsAndCancellation()
    {
        await Assert.That(() => new RgbaImage(0, 1, [])).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new RgbaImage(1, 1, [1, 2, 3])).Throws<ArgumentException>();
        await Assert.That(() => RgbaImage.Create(int.MaxValue, int.MaxValue, 1, 2, 3)).Throws<ImageLimitException>();
        await Assert.That(() => RgbaImage.Create(int.MaxValue, int.MaxValue, 1, 2, 3, limits: new ImageLimits
        {
            MaxWidth = int.MaxValue,
            MaxHeight = int.MaxValue,
            MaxPixels = long.MaxValue,
            MaxDecodedBytes = long.MaxValue
        })).Throws<ImageLimitException>();
        var image = RgbaImage.Create(2, 1, 1, 2, 3);
        await Assert.That(() => image.Resize(0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => image.Resize(4, new ImageLimits { MaxPixels = 7 })).Throws<ImageLimitException>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(() => image.Resize(3, cancellationToken: cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(() => image.FillRectangle(0, 0, 1, 1, 1, 2, 3, cancellationToken: cancellation.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(() => image.Composite(image, 0, 0, cancellationToken: cancellation.Token))
            .Throws<OperationCanceledException>();
    }

    private readonly record struct Chunk(string Type, byte[] Data);

    private static async Task BytesEqual(byte[] actual, byte[] expected) =>
        await Assert.That(actual.SequenceEqual(expected)).IsTrue();

    private sealed class PartialReadStream(byte[] bytes, Action? afterRead = null) : Stream
    {
        private int position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty || position == bytes.Length)
                return 0;
            buffer[0] = bytes[position++];
            afterRead?.Invoke();
            return 1;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static Chunk Header(int width = 1, int height = 1, byte color = 6, byte depth = 8, byte interlace = 0) =>
        new("IHDR", [.. UInt32((uint)width), .. UInt32((uint)height), depth, color, 0, 0, interlace]);

    private static byte[] UInt32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] Compress(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(bytes);
        return output.ToArray();
    }

    private static byte[] Png(params Chunk[] chunks)
    {
        using var output = new MemoryStream();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        foreach (var chunk in chunks)
        {
            output.Write(UInt32((uint)chunk.Data.Length));
            var body = Encoding.ASCII.GetBytes(chunk.Type).Concat(chunk.Data).ToArray();
            output.Write(body);
            output.Write(UInt32(Crc(body)));
        }
        return output.ToArray();
    }

    private static List<Chunk> ReadChunks(byte[] bytes)
    {
        var chunks = new List<Chunk>();
        for (var offset = 8; offset < bytes.Length;)
        {
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4)));
            var body = bytes.AsSpan(offset + 4, length + 4);
            if (Crc(body) != BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + length + 8, 4)))
                throw new InvalidOperationException("Encoded PNG chunk has an invalid checksum.");
            chunks.Add(new(Encoding.ASCII.GetString(body[..4]), body[4..].ToArray()));
            offset = checked(offset + length + 12);
        }
        return chunks;
    }

    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320u);
        }
        return ~crc;
    }
}
