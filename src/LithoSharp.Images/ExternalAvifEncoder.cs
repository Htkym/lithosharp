using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Buffers.Binary;

namespace LithoSharp.Images;

/// <summary>Explicit avifenc adapter. No executable is discovered or downloaded automatically.</summary>
/// <remarks>Create a new instance after changing the tool or its codec dependencies. The dependency fingerprint should identify the installed codec/runtime versions.</remarks>
public sealed class ExternalAvifEncoder
{
    private readonly string binaryHash;

    /// <summary>Configures a trusted avifenc executable and fingerprints its bytes and codec dependencies.</summary>
    /// <exception cref="ArgumentException">A path or fingerprint is empty, or a path component is a symbolic link.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Timeout is not positive or exceeds one hour.</exception>
    /// <exception cref="IOException">The executable cannot be read.</exception>
    public ExternalAvifEncoder(string executablePath, string dependencyFingerprint, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(dependencyFingerprint);
        ExecutablePath = Path.GetFullPath(executablePath);
        Timeout = timeout ?? TimeSpan.FromMinutes(2);
        if (Timeout <= TimeSpan.Zero || Timeout > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        binaryHash = HashExecutable();
        Fingerprint = $"avifenc-adapter/1:{binaryHash}:{dependencyFingerprint}:speed=6:jobs=1:alpha=100";
    }

    /// <summary>Absolute path to the explicitly selected executable.</summary>
    public string ExecutablePath { get; }
    /// <summary>Maximum duration for each invocation.</summary>
    public TimeSpan Timeout { get; }
    /// <summary>Fingerprint of the tool bytes, dependency versions and fixed encoder settings.</summary>
    public string Fingerprint { get; }

    internal async Task<byte[]> EncodeAsync(byte[] png, int quality, CancellationToken cancellationToken)
    {
        await ValidateAsync(cancellationToken).ConfigureAwait(false);
        var directory = Directory.CreateTempSubdirectory("lithosharp-avif-").FullName;
        try
        {
            var input = Path.Combine(directory, "input.png");
            var output = Path.Combine(directory, "output.avif");
            await File.WriteAllBytesAsync(input, png, cancellationToken).ConfigureAwait(false);
            var start = new ProcessStartInfo(ExecutablePath)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = directory
            };
            foreach (var argument in new[] { "--no-overwrite", "--qcolor", quality.ToString(CultureInfo.InvariantCulture),
                "--qalpha", "100", "--speed", "6", "--jobs", "1", "--", input, output })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new IOException("Cannot start avifenc.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            var stdout = DrainAsync(process.StandardOutput, timeout.Token);
            var stderr = DrainAsync(process.StandardError, timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); } catch (OperationCanceledException) { }
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException($"avifenc exceeded {Timeout}.");
            }
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException($"avifenc failed with exit code {process.ExitCode}.");
            if ((File.GetAttributes(output) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new IOException("avifenc output must be a regular file.");
            var bytes = await File.ReadAllBytesAsync(output, cancellationToken).ConfigureAwait(false);
            if (!HasAvifBrand(bytes))
                throw new InvalidDataException("avifenc did not produce an AVIF container.");
            return bytes;
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    internal Task ValidateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (HashExecutable() != binaryHash) throw new InvalidOperationException("The AVIF executable changed. Create a new encoder declaration.");
        return Task.CompletedTask;
    }

    private static bool HasAvifBrand(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 16 || !bytes.Slice(4, 4).SequenceEqual("ftyp"u8)) return false;
        var size = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        if (size < 16 || size > bytes.Length || size % 4 != 0) return false;
        for (var offset = 8; offset < size; offset += 4)
        {
            if (offset == 12) continue;
            var brand = bytes.Slice(offset, 4);
            if (brand.SequenceEqual("avif"u8) || brand.SequenceEqual("avis"u8)) return true;
        }
        return false;
    }

    private string HashExecutable()
    {
        for (var path = ExecutablePath; path is not null; path = Path.GetDirectoryName(path))
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("The AVIF executable path must not contain symbolic links.", nameof(ExecutablePath));
        using var stream = new FileStream(ExecutablePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static async Task DrainAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) != 0) { }
    }
}
