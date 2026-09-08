using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LithoSharp.Build;

internal static class BuildInputFingerprint
{
    private const uint FileNameNormalized = 0;

    public static string FromFile(string path)
    {
        using var stream = OpenRead(path);
        EnsureRegularFile(stream.SafeFileHandle, path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static string FromContainedFile(string inputRoot, string relativePath)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(inputRoot));
        var fullPath = Path.GetFullPath(Path.Combine(
            fullRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        using var stream = OpenVerifiedContainedRead(fullRoot, fullPath);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    internal static FileStream OpenVerifiedContainedRead(
        string inputRoot,
        string path,
        bool asynchronous = false)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(inputRoot));
        var fullPath = Path.GetFullPath(path);
        SiteGenerator.EnsureContainedPathHasNoNameSurrogateReparsePoints(
            fullRoot,
            fullPath);

        var stream = OpenRead(fullPath, asynchronous);
        var verified = false;
        try
        {
            EnsureRegularFile(stream.SafeFileHandle, fullPath);
            var finalPath = GetFinalPath(stream.SafeFileHandle);
            EnsureContainedFinalPath(fullRoot, fullPath, finalPath);
            verified = true;
            return stream;
        }
        finally
        {
            if (!verified)
            {
                stream.Dispose();
            }
        }
    }

    private static FileStream OpenRead(string path, bool asynchronous = false) =>
        new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: asynchronous ? 1 : 81920,
            FileOptions.SequentialScan
                | (asynchronous ? FileOptions.Asynchronous : FileOptions.None));

    private static void EnsureRegularFile(SafeFileHandle handle, string path)
    {
        var attributes = File.GetAttributes(handle);
        if ((attributes
             & (FileAttributes.Directory
                | FileAttributes.Device
                | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidOperationException(
                $"Declared file dependency '{path}' is not a regular file.");
        }

        _ = RandomAccess.GetLength(handle);
    }

    private static void EnsureContainedFinalPath(
        string root,
        string expectedPath,
        string finalPath)
    {
        var fullFinalPath = Path.GetFullPath(finalPath);
        var relativePath = Path.GetRelativePath(root, fullFinalPath);
        if (Path.IsPathRooted(relativePath)
            || relativePath == ".."
            || relativePath.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal)
            || relativePath.StartsWith(
                $"..{Path.AltDirectorySeparatorChar}",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Opened dependency path '{fullFinalPath}' escapes input root '{root}'.");
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(fullFinalPath, expectedPath, comparison))
        {
            throw new InvalidOperationException(
                $"Opened dependency path '{fullFinalPath}' does not match requested path '{expectedPath}'.");
        }
    }

    private static string GetFinalPath(SafeFileHandle handle)
    {
        if (OperatingSystem.IsWindows())
        {
            return GetWindowsFinalPath(handle);
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var descriptorPath = OperatingSystem.IsLinux()
                ? $"/proc/self/fd/{handle.DangerousGetHandle().ToInt64()}"
                : $"/dev/fd/{handle.DangerousGetHandle().ToInt64()}";
            return File.ResolveLinkTarget(descriptorPath, returnFinalTarget: true)?.FullName
                ?? throw new IOException(
                    $"Could not resolve the opened dependency handle '{descriptorPath}'.");
        }

        throw new PlatformNotSupportedException(
            "Validating an opened dependency's final path is supported on Windows, Linux, and macOS.");
    }

    [SupportedOSPlatform("windows")]
    private static string GetWindowsFinalPath(SafeFileHandle handle)
    {
        var capacity = 512;
        while (true)
        {
            var buffer = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandle(
                handle,
                buffer,
                (uint)buffer.Capacity,
                FileNameNormalized);
            if (length == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            if (length < buffer.Capacity)
            {
                return NormalizeWindowsDevicePath(buffer.ToString());
            }

            capacity = checked((int)length + 1);
        }
    }

    private static string NormalizeWindowsDevicePath(string path)
    {
        const string devicePrefix = @"\\?\";
        const string uncPrefix = @"\\?\UNC\";
        if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[uncPrefix.Length..];
        }

        return path.StartsWith(devicePrefix, StringComparison.OrdinalIgnoreCase)
            ? path[devicePrefix.Length..]
            : path;
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFinalPathNameByHandleW",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    [SupportedOSPlatform("windows")]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        StringBuilder path,
        uint pathLength,
        uint flags);
}
