using LithoSharp.Configuration;
using LithoSharp.Diagnostics;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace LithoSharp.Tests;

[NotInParallel]
public sealed class SiteGeneratorAtomicOutputTests
{
    [Test]
    public async Task GenerateAsync_CleanSuccessReplacesExistingOutput()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "stale.txt"), "stale");

        var result = await GenerateAsync(
            output,
            clean: true,
            new SingleFileTemplate("index.html", "current"));

        await Assert.That(File.Exists(Path.Combine(output, "stale.txt"))).IsFalse();
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "index.html")))
            .IsEqualTo("current");
        await Assert.That(result.GeneratedFiles).IsEquivalentTo(
            [Path.Combine(output, "index.html")]);
        await AssertNoTransactionDirectoriesAsync(workspace.Root);
    }

    [Test]
    public async Task GenerateAsync_WriteFailurePreservesExistingOutput()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        Directory.CreateDirectory(output);
        var sentinel = Path.Combine(output, "keep.txt");
        var blockingFile = Path.Combine(output, "assets");
        await File.WriteAllTextAsync(sentinel, "unchanged");
        await File.WriteAllTextAsync(blockingFile, "existing file");

        await Assert.That(async () => await GenerateAsync(
                output,
                clean: false,
                new SingleFileTemplate("assets/site.css", "new")))
            .Throws<IOException>();

        await Assert.That(await File.ReadAllTextAsync(sentinel)).IsEqualTo("unchanged");
        await Assert.That(await File.ReadAllTextAsync(blockingFile)).IsEqualTo("existing file");
        await AssertNoTransactionDirectoriesAsync(workspace.Root);
    }

    [Test]
    public async Task GenerateAsync_CancellationDuringStagingPreservesExistingOutput()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        Directory.CreateDirectory(output);
        var sentinel = Path.Combine(output, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "unchanged");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.That(async () => await GenerateAsync(
                output,
                clean: false,
                new SingleFileTemplate("index.html", "new", observeCancellation: false),
                cancellation.Token))
            .Throws<OperationCanceledException>();

        await Assert.That(await File.ReadAllTextAsync(sentinel)).IsEqualTo("unchanged");
        await Assert.That(File.Exists(Path.Combine(output, "index.html"))).IsFalse();
        await AssertNoTransactionDirectoriesAsync(workspace.Root);
    }

    [Test]
    public async Task GenerateAsync_OutputTreeSymlinkIsRejectedWithoutFollowingIt()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        var outside = Path.Combine(workspace.Root, "outside");
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "unchanged");
        if (!TryCreateDirectorySymbolicLink(Path.Combine(output, "escape"), outside))
        {
            return;
        }

        await Assert.That(async () => await GenerateAsync(
                output,
                clean: false,
                new SingleFileTemplate("index.html", "new")))
            .Throws<InvalidOperationException>()
            .WithMessageContaining("symbolic link or name-surrogate reparse point");

        await Assert.That(await File.ReadAllTextAsync(sentinel)).IsEqualTo("unchanged");
        await AssertNoTransactionDirectoriesAsync(workspace.Root);
    }

    [Test]
    public async Task GenerateAsync_WindowsFileLockPreservesExistingOutput()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        Directory.CreateDirectory(output);
        var sentinel = Path.Combine(output, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "unchanged");
        using var locked = new FileStream(
            sentinel,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        await Assert.That(async () => await GenerateAsync(
                output,
                clean: true,
                new SingleFileTemplate("index.html", "new")))
            .Throws<IOException>();

        await Assert.That(await File.ReadAllTextAsync(sentinel)).IsEqualTo("unchanged");
        await Assert.That(File.Exists(Path.Combine(output, "index.html"))).IsFalse();
        await AssertNoTransactionDirectoriesAsync(workspace.Root);
    }

    [Test]
    public async Task GenerateAsync_ConcurrentCleanFalseGenerationsLeaveOnlyLatestOwnedOutput()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        using var ready = new CountdownEvent(2);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = GenerateAsync(
            output,
            clean: false,
            new GatedTemplate("first.txt", "first", ready, release.Task));
        var second = GenerateAsync(
            output,
            clean: false,
            new GatedTemplate("second.txt", "second", ready, release.Task));
        await Assert.That(await Task.Run(() => ready.Wait(TimeSpan.FromSeconds(5)))).IsTrue();
        release.SetResult();

        await Task.WhenAll(first, second);

        var remaining = Directory.EnumerateFiles(output, "*.txt")
            .Select(Path.GetFileName)
            .ToArray();
        await Assert.That(remaining.Length).IsEqualTo(1);
        await Assert.That(remaining.Single() is "first.txt" or "second.txt").IsTrue();
        await AssertNoTransactionDirectoriesAsync(workspace.Root);
    }

    [Test]
    public async Task GenerateAsync_CleanFalseWithoutManifestPreservesLegacyAndUserFiles()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        Directory.CreateDirectory(output);
        var legacy = Path.Combine(output, "legacy-generated.html");
        var user = Path.Combine(output, "user.txt");
        await File.WriteAllTextAsync(legacy, "legacy");
        await File.WriteAllTextAsync(user, "user");

        await GenerateAsync(
            output,
            clean: false,
            new SingleFileTemplate("index.html", "current"));

        await Assert.That(await File.ReadAllTextAsync(legacy)).IsEqualTo("legacy");
        await Assert.That(await File.ReadAllTextAsync(user)).IsEqualTo("user");
        await Assert.That(GetOwnershipStatePath(workspace.Root)).IsNotNull();
    }

    [Test]
    public async Task GenerateAsync_OwnershipStateIsOwnerRestrictedOutsideOutput()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");

        await GenerateAsync(
            output,
            clean: false,
            new SingleFileTemplate("index.html", "current"));

        var statePath = GetOwnershipStatePath(workspace.Root);
        await Assert.That(Path.GetDirectoryName(statePath)).IsEqualTo(workspace.Root);
        await Assert.That(statePath.StartsWith(
            output + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase)).IsFalse();
        if (!OperatingSystem.IsWindows())
        {
            await Assert.That(File.GetUnixFileMode(statePath)).IsEqualTo(
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Test]
    public async Task GenerateAsync_PublishedManifestIsNotDeletionAuthority()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        Directory.CreateDirectory(output);
        var outside = Path.Combine(workspace.Root, "outside.txt");
        await File.WriteAllTextAsync(outside, "unchanged");
        await File.WriteAllTextAsync(
            Path.Combine(output, ".lithosharp-output-manifest.json"),
            """{"version":1,"files":["../outside.txt"]}""");

        await GenerateAsync(
            output,
            clean: false,
            new SingleFileTemplate("index.html", "current"));

        await Assert.That(await File.ReadAllTextAsync(outside)).IsEqualTo("unchanged");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "index.html")))
            .IsEqualTo("current");
        await AssertNoTransactionDirectoriesAsync(workspace.Root);
    }

    [Test]
    public async Task GenerateAsync_CleanFalseRemovesUnmodifiedStaleGeneratedFile()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        await GenerateAsync(
            output,
            clean: false,
            new SingleFileTemplate("old.html", "generated"));

        await GenerateAsync(
            output,
            clean: false,
            new SingleFileTemplate("current.html", "current"));

        await Assert.That(File.Exists(Path.Combine(output, "old.html"))).IsFalse();
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "current.html")))
            .IsEqualTo("current");
    }

    [Test]
    public async Task GenerateAsync_CleanFalsePreservesModifiedStaleGeneratedFile()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        var stale = Path.Combine(output, "old.html");
        await GenerateAsync(
            output,
            clean: false,
            new SingleFileTemplate("old.html", "generated"));
        await File.WriteAllTextAsync(stale, "user-modified");

        await GenerateAsync(
            output,
            clean: false,
            new SingleFileTemplate("current.html", "current"));

        await Assert.That(await File.ReadAllTextAsync(stale)).IsEqualTo("user-modified");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "current.html")))
            .IsEqualTo("current");
    }

    [Test]
    public async Task GenerateAsync_TamperedOwnershipIdentityFailsWithoutChangingOutput()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        var original = Path.Combine(output, "original.html");
        await GenerateAsync(
            output,
            clean: false,
            new SingleFileTemplate("original.html", "original"));
        var statePath = GetOwnershipStatePath(workspace.Root);
        await File.WriteAllTextAsync(
            statePath,
            $$"""{"version":1,"outputIdentity":"{{new string('0', 64)}}","artifacts":[]}""");

        await Assert.That(async () => await GenerateAsync(
                output,
                clean: false,
                new SingleFileTemplate("current.html", "current")))
            .Throws<InvalidOperationException>()
            .WithMessageContaining("canonical output identity");

        await Assert.That(await File.ReadAllTextAsync(original)).IsEqualTo("original");
        await Assert.That(File.Exists(Path.Combine(output, "current.html"))).IsFalse();
    }

    [Test]
    public async Task GenerateAsync_TamperedOwnershipPathCannotEscapeOutput()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        var original = Path.Combine(output, "original.html");
        var outside = Path.Combine(workspace.Root, "outside.txt");
        await GenerateAsync(
            output,
            clean: false,
            new SingleFileTemplate("original.html", "original"));
        await File.WriteAllTextAsync(outside, "outside");
        var statePath = GetOwnershipStatePath(workspace.Root);
        using var state = JsonDocument.Parse(await File.ReadAllBytesAsync(statePath));
        var outputIdentity = state.RootElement
            .GetProperty("outputIdentity")
            .GetString()!;
        await File.WriteAllTextAsync(
            statePath,
            $$"""
              {"version":1,"outputIdentity":"{{outputIdentity}}","artifacts":[{"path":"../outside.txt","sha256":"{{new string('0', 64)}}"}]}
              """);

        await Assert.That(async () => await GenerateAsync(
                output,
                clean: false,
                new SingleFileTemplate("current.html", "current")))
            .Throws<InvalidOperationException>()
            .WithMessageContaining("unsafe path");

        await Assert.That(await File.ReadAllTextAsync(outside)).IsEqualTo("outside");
        await Assert.That(await File.ReadAllTextAsync(original)).IsEqualTo("original");
        await Assert.That(File.Exists(Path.Combine(output, "current.html"))).IsFalse();
    }

    [Test]
    public async Task GenerateAsync_CorruptOwnershipStateFailsWithoutChangingOutput()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        var original = Path.Combine(output, "original.html");
        await GenerateAsync(
            output,
            clean: false,
            new SingleFileTemplate("original.html", "original"));
        await File.WriteAllTextAsync(GetOwnershipStatePath(workspace.Root), "{");

        await Assert.That(async () => await GenerateAsync(
                output,
                clean: false,
                new SingleFileTemplate("current.html", "current")))
            .Throws<InvalidOperationException>()
            .WithMessageContaining("is invalid");

        await Assert.That(await File.ReadAllTextAsync(original)).IsEqualTo("original");
        await Assert.That(File.Exists(Path.Combine(output, "current.html"))).IsFalse();
    }

    [Test]
    public async Task GenerateAsync_FailedGenerationKeepsPriorOwnershipState()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        await GenerateAsync(
            output,
            clean: false,
            new SingleFileTemplate("original.html", "original"));
        var statePath = GetOwnershipStatePath(workspace.Root);
        var priorState = await File.ReadAllBytesAsync(statePath);
        await File.WriteAllTextAsync(Path.Combine(output, "assets"), "blocking");

        await Assert.That(async () => await GenerateAsync(
                output,
                clean: false,
                new SingleFileTemplate("assets/site.css", "new")))
            .Throws<IOException>();

        await Assert.That(await File.ReadAllBytesAsync(statePath)).IsEquivalentTo(priorState);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "original.html")))
            .IsEqualTo("original");
    }

    [Test]
    public async Task GenerateAsync_CanceledGenerationKeepsPriorOwnershipState()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        await GenerateAsync(
            output,
            clean: false,
            new SingleFileTemplate("original.html", "original"));
        var statePath = GetOwnershipStatePath(workspace.Root);
        var priorState = await File.ReadAllBytesAsync(statePath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.That(async () => await GenerateAsync(
                output,
                clean: false,
                new SingleFileTemplate(
                    "current.html",
                    "current",
                    observeCancellation: false),
                cancellation.Token))
            .Throws<OperationCanceledException>();

        await Assert.That(await File.ReadAllBytesAsync(statePath)).IsEquivalentTo(priorState);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "original.html")))
            .IsEqualTo("original");
        await Assert.That(File.Exists(Path.Combine(output, "current.html"))).IsFalse();
    }

    [Test]
    public async Task GenerateAsync_LockAcquisitionHonorsCancellation()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        var lockPath = Path.Combine(
            workspace.Root,
            $".lithosharp-lock-{CreateLockIdentity(output)}.lock");
        await using var heldLock = new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.That(async () => await GenerateAsync(
                output,
                clean: false,
                new SingleFileTemplate("index.html", "new"),
                cancellation.Token))
            .Throws<OperationCanceledException>();

        await Assert.That(Directory.Exists(output)).IsFalse();
        await AssertNoTransactionDirectoriesAsync(workspace.Root);
    }

    [Test]
    public async Task GenerateAsync_CaseAndUnicodeAliasesShareConservativeLockIdentity()
    {
        using var workspace = new TemporaryWorkspace();
        var composed = Path.Combine(workspace.Root, "Café");
        var decomposedUpper = Path.Combine(workspace.Root, "CAFE\u0301");

        await GenerateAsync(
            composed,
            clean: true,
            new SingleFileTemplate("first.txt", "first"));
        await GenerateAsync(
            decomposedUpper,
            clean: true,
            new SingleFileTemplate("second.txt", "second"));

        await Assert.That(Directory.EnumerateFiles(
                workspace.Root,
                ".lithosharp-lock-*.lock")
            .Count()).IsEqualTo(1);
        await Assert.That(CreateLockIdentity(composed)).IsEqualTo(
            CreateLockIdentity(decomposedUpper));
    }

    [Test]
    public async Task GenerateAsync_NfcAndNfdDistinctDirectoriesKeepSeparateOwnershipState()
    {
        using var workspace = new TemporaryWorkspace();
        var composed = Path.Combine(workspace.Root, "Caf\u00e9");
        var decomposed = Path.Combine(workspace.Root, "Cafe\u0301");
        Directory.CreateDirectory(composed);
        Directory.CreateDirectory(decomposed);
        var directoryNames = Directory.EnumerateDirectories(workspace.Root)
            .Select(Path.GetFileName)
            .ToArray();
        if (!directoryNames.Contains("Caf\u00e9", StringComparer.Ordinal)
            || !directoryNames.Contains("Cafe\u0301", StringComparer.Ordinal))
        {
            return;
        }

        await GenerateAsync(
            composed,
            clean: false,
            new SingleFileTemplate("first.txt", "first"));
        await GenerateAsync(
            decomposed,
            clean: false,
            new SingleFileTemplate("second.txt", "second"));
        var ownershipStates = Directory.EnumerateFiles(
                workspace.Root,
                ".lithosharp-ownership-*.json")
            .Where(path => !Path.GetFileName(path).StartsWith(
                ".lithosharp-ownership-pending-",
                StringComparison.Ordinal))
            .ToArray();
        var decomposedState = ownershipStates.Single(path =>
            File.ReadAllText(path).Contains("second.txt", StringComparison.Ordinal));
        var decomposedStateBytes = await File.ReadAllBytesAsync(decomposedState);

        await GenerateAsync(
            composed,
            clean: false,
            new SingleFileTemplate("third.txt", "third"));

        await Assert.That(ownershipStates.Length).IsEqualTo(2);
        await Assert.That(File.Exists(Path.Combine(composed, "first.txt"))).IsFalse();
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(composed, "third.txt")))
            .IsEqualTo("third");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(decomposed, "second.txt")))
            .IsEqualTo("second");
        await Assert.That(await File.ReadAllBytesAsync(decomposedState))
            .IsEquivalentTo(decomposedStateBytes);
        await Assert.That(Directory.EnumerateFiles(
                workspace.Root,
                ".lithosharp-ownership-*.json")
            .Count(path => !Path.GetFileName(path).StartsWith(
                ".lithosharp-ownership-pending-",
                StringComparison.Ordinal))).IsEqualTo(2);
        await Assert.That(Directory.EnumerateFiles(
                workspace.Root,
                ".lithosharp-lock-*.lock")
            .Count()).IsEqualTo(1);
    }

    [Test]
    public async Task GenerateAsync_ChildProcessLockContentionHonorsCancellation()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TemporaryWorkspace();
        var heldOutput = Path.Combine(workspace.Root, "Café");
        var equivalentOutput = Path.Combine(workspace.Root, "CAFE\u0301");
        var lockPath = Path.Combine(
            workspace.Root,
            $".lithosharp-lock-{CreateLockIdentity(heldOutput)}.lock");
        var readyPath = Path.Combine(workspace.Root, "child-ready");
        var stopPath = Path.Combine(workspace.Root, "child-stop");
        // Windows PowerShell uses .NET Framework and needs extended syntax for long fixture paths.
        var childLockPath = @"\\?\" + lockPath;
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $stream = [System.IO.FileStream]::new(
                '{{childLockPath.Replace("'", "''", StringComparison.Ordinal)}}',
                [System.IO.FileMode]::OpenOrCreate,
                [System.IO.FileAccess]::ReadWrite,
                [System.IO.FileShare]::None)
            [System.IO.File]::WriteAllText(
                '{{readyPath.Replace("'", "''", StringComparison.Ordinal)}}',
                'ready')
            try {
                while (-not [System.IO.File]::Exists(
                    '{{stopPath.Replace("'", "''", StringComparison.Ordinal)}}')) {
                    Start-Sleep -Milliseconds 20
                }
            }
            finally {
                $stream.Dispose()
            }
            """;
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(
                Environment.SystemDirectory,
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe"),
            Arguments = $"-NoProfile -NonInteractive -EncodedCommand "
                + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Failed to start child PowerShell process.");

        try
        {
            await WaitForFileAsync(readyPath, TimeSpan.FromSeconds(30));
            using var cancellation =
                new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

            await Assert.That(async () => await GenerateAsync(
                    equivalentOutput,
                    clean: false,
                    new SingleFileTemplate("index.html", "new"),
                    cancellation.Token))
                .Throws<OperationCanceledException>();
        }
        finally
        {
            await File.WriteAllTextAsync(stopPath, "stop");
            using var exitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await process.WaitForExitAsync(exitTimeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    [Test]
    public async Task GenerateAsync_CleanFalsePreservesUntouchedMetadata()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        var directory = Path.Combine(output, "preserved");
        var file = Path.Combine(directory, "tool");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(file, "unchanged");
        var rootTimestamp = new DateTime(2020, 1, 2, 3, 4, 6, DateTimeKind.Utc);
        var directoryTimestamp = rootTimestamp.AddSeconds(1);
        var fileTimestamp = rootTimestamp.AddSeconds(2);
        var fileAccessTimestamp = rootTimestamp.AddSeconds(3);
        var fileCreationTimestamp = rootTimestamp.AddSeconds(4);
        File.SetLastWriteTimeUtc(file, fileTimestamp);
        File.SetLastAccessTimeUtc(file, fileAccessTimestamp);
        Directory.SetLastWriteTimeUtc(directory, directoryTimestamp);
        Directory.SetLastWriteTimeUtc(output, rootTimestamp);

        if (OperatingSystem.IsWindows())
        {
            File.SetCreationTimeUtc(file, fileCreationTimestamp);
            File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.Hidden);
            File.SetAttributes(output, File.GetAttributes(output) | FileAttributes.Hidden);
        }
        else
        {
            File.SetUnixFileMode(
                output,
                UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead
                | UnixFileMode.GroupExecute);
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute);
            File.SetUnixFileMode(
                file,
                UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead
                | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead
                | UnixFileMode.OtherExecute);
        }

        await GenerateAsync(
            output,
            clean: false,
            new SingleFileTemplate("index.html", "current"));

        await Assert.That(File.GetLastWriteTimeUtc(file)).IsEqualTo(fileTimestamp);
        await Assert.That(File.GetLastAccessTimeUtc(file)).IsEqualTo(fileAccessTimestamp);
        await Assert.That(Directory.GetLastWriteTimeUtc(directory)).IsEqualTo(directoryTimestamp);
        await Assert.That(Directory.GetLastWriteTimeUtc(output)).IsEqualTo(rootTimestamp);
        if (OperatingSystem.IsWindows())
        {
            await Assert.That(File.GetCreationTimeUtc(file)).IsEqualTo(fileCreationTimestamp);
            await Assert.That((File.GetAttributes(file) & FileAttributes.Hidden) != 0).IsTrue();
            await Assert.That((File.GetAttributes(output) & FileAttributes.Hidden) != 0).IsTrue();
        }
        else
        {
            await Assert.That(File.GetUnixFileMode(output)).IsEqualTo(
                UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead
                | UnixFileMode.GroupExecute);
            await Assert.That(File.GetUnixFileMode(directory)).IsEqualTo(
                UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute);
            await Assert.That(File.GetUnixFileMode(file)).IsEqualTo(
                UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead
                | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead
                | UnixFileMode.OtherExecute);
        }
    }

    [Test]
    public async Task GenerateAsync_CleanFalseOverwritesRestrictiveFileAndRestoresAttributes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        Directory.CreateDirectory(output);
        var generatedFile = Path.Combine(output, "index.html");
        await File.WriteAllTextAsync(generatedFile, "old");
        File.SetAttributes(
            generatedFile,
            File.GetAttributes(generatedFile)
            | FileAttributes.ReadOnly
            | FileAttributes.System);

        try
        {
            await GenerateAsync(
                output,
                clean: false,
                new SingleFileTemplate("index.html", "current"));

            await Assert.That(await File.ReadAllTextAsync(generatedFile)).IsEqualTo("current");
            var attributes = File.GetAttributes(generatedFile);
            await Assert.That((attributes & FileAttributes.ReadOnly) != 0).IsTrue();
            await Assert.That((attributes & FileAttributes.System) != 0).IsTrue();
        }
        finally
        {
            if (File.Exists(generatedFile))
            {
                var attributes = File.GetAttributes(generatedFile);
                File.SetAttributes(
                    generatedFile,
                    attributes & ~(FileAttributes.ReadOnly | FileAttributes.System));
            }
        }
    }

    [Test]
    public async Task GenerateAsync_CleanSuccessDeletesRestrictiveBackup()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        Directory.CreateDirectory(output);
        var oldFile = Path.Combine(output, "old.txt");
        await File.WriteAllTextAsync(oldFile, "old");
        File.SetAttributes(
            oldFile,
            File.GetAttributes(oldFile)
            | FileAttributes.ReadOnly
            | FileAttributes.System);

        await GenerateAsync(
            output,
            clean: true,
            new SingleFileTemplate("index.html", "current"));

        await Assert.That(File.Exists(oldFile)).IsFalse();
        await Assert.That(FindTransactionDirectories(workspace.Root, "backup")).IsEmpty();
    }

    [Test]
    public async Task GenerateAsync_RecoversOnlyRegisteredAbandonedStaging()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        await GenerateAsync(
            output,
            clean: true,
            new SingleFileTemplate("initial.txt", "initial"));
        var lockIdentity = CreateLockIdentity(output);
        var registered = Path.Combine(
            workspace.Root,
            $".lithosharp-staging-{lockIdentity}-{Guid.NewGuid():N}");
        var unregistered = Path.Combine(
            workspace.Root,
            $".lithosharp-staging-{lockIdentity}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(registered);
        Directory.CreateDirectory(unregistered);
        var restrictive = Path.Combine(registered, "restrictive.txt");
        await File.WriteAllTextAsync(restrictive, "stale");
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(
                restrictive,
                File.GetAttributes(restrictive)
                | FileAttributes.ReadOnly
                | FileAttributes.System);
        }

        var lockPath = Path.Combine(
            workspace.Root,
            $".lithosharp-lock-{lockIdentity}.lock");
        await File.WriteAllTextAsync(
            lockPath,
            $"S|{Convert.ToBase64String(Encoding.UTF8.GetBytes(Path.GetFullPath(output)))}"
            + $"|{Convert.ToBase64String(Encoding.UTF8.GetBytes(registered))}\n");

        await GenerateAsync(
            output,
            clean: false,
            new SingleFileTemplate("current.txt", "current"));

        await Assert.That(Directory.Exists(registered)).IsFalse();
        await Assert.That(Directory.Exists(unregistered)).IsTrue();
        await Assert.That(await File.ReadAllTextAsync(lockPath))
            .DoesNotContain("S|");
    }

    [Test]
    public async Task GenerateAsync_RestoresRegisteredBackupAfterInterruptedDirectorySwap()
    {
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        await GenerateAsync(
            output,
            clean: true,
            new SingleFileTemplate("index.html", "initial"));
        var lockIdentity = CreateLockIdentity(output);
        var backup = Path.Combine(
            workspace.Root,
            $".lithosharp-backup-{lockIdentity}-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(Path.Combine(output, "assets"), "existing file");
        Directory.Move(output, backup);
        var lockPath = Path.Combine(
            workspace.Root,
            $".lithosharp-lock-{lockIdentity}.lock");
        await File.WriteAllTextAsync(
            lockPath,
            $"B|{Convert.ToBase64String(Encoding.UTF8.GetBytes(Path.GetFullPath(output)))}"
            + $"|{Convert.ToBase64String(Encoding.UTF8.GetBytes(backup))}\n");

        // The next build fails while staging, after recovery has restored the entire prior tree.
        await Assert.That(async () => await GenerateAsync(
                output,
                clean: false,
                new SingleFileTemplate("assets/site.css", "new")))
            .Throws<IOException>();

        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "index.html"))).IsEqualTo("initial");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "assets"))).IsEqualTo("existing file");
        await Assert.That(Directory.Exists(backup)).IsFalse();
        await Assert.That(await File.ReadAllTextAsync(lockPath)).DoesNotContain("B|");
        await AssertNoTransactionDirectoriesAsync(workspace.Root);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, true)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    public async Task Lsr22_RegisteredStagingRestartsAcrossNamespace(bool recordedExtended, bool restartExtended)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        await GenerateAsync(output, true, new SingleFileTemplate("initial.txt", "initial"));
        var identity = CreateLockIdentity(output);
        var registered = Path.Combine(workspace.Root, $".lithosharp-staging-{identity}-{Guid.NewGuid():N}");
        var unregistered = Path.Combine(workspace.Root, $".lithosharp-staging-{identity}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(registered);
        Directory.CreateDirectory(unregistered);
        await File.WriteAllTextAsync(Path.Combine(registered, "stale.txt"), "registered");
        await File.WriteAllTextAsync(Path.Combine(unregistered, "keep.txt"), "unregistered");
        var journal = Lsr22WriteJournal(workspace.Root, output, "S", registered, recordedExtended);
        await Lsr22ObserveRecoveryAsync(
            () => GenerateAsync(Lsr22Namespace(output, restartExtended), false, new SingleFileTemplate("current.txt", "current")),
            workspace.Root, output, registered, journal, "staging", recordedExtended, restartExtended);
        await Assert.That(Directory.Exists(registered)).IsFalse();
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(unregistered, "keep.txt"))).IsEqualTo("unregistered");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "current.txt"))).IsEqualTo("current");
        await Assert.That(await File.ReadAllTextAsync(journal)).DoesNotContain("S|");
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, true)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    public async Task Lsr22_RegisteredBackupRestartsAcrossNamespace(bool recordedExtended, bool restartExtended)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        await GenerateAsync(output, true, new SingleFileTemplate("index.html", "initial"));
        await File.WriteAllTextAsync(Path.Combine(output, "assets"), "existing file");
        var identity = CreateLockIdentity(output);
        var backup = Path.Combine(workspace.Root, $".lithosharp-backup-{identity}-{Guid.NewGuid():N}");
        Directory.Move(output, backup);
        var journal = Lsr22WriteJournal(workspace.Root, output, "B", backup, recordedExtended);
        // The restored tree blocks this staging write, exactly as the existing
        // interrupted-swap test. Recovery must complete before this IOException.
        await Assert.That(async () => await Lsr22ObserveRecoveryAsync(
                () => GenerateAsync(Lsr22Namespace(output, restartExtended), false, new SingleFileTemplate("assets/site.css", "new")),
                workspace.Root, output, backup, journal, "backup", recordedExtended, restartExtended))
            .Throws<IOException>();
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "index.html"))).IsEqualTo("initial");
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "assets"))).IsEqualTo("existing file");
        await Assert.That(Directory.Exists(backup)).IsFalse();
        await Assert.That(await File.ReadAllTextAsync(journal)).DoesNotContain("B|");
        await AssertNoTransactionDirectoriesAsync(workspace.Root);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, true)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    public async Task Lsr22_PendingOwnershipRestartsAcrossNamespace(bool recordedExtended, bool restartExtended)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        await GenerateAsync(output, true, new SingleFileTemplate("initial.txt", "initial"));
        var identity = CreateLockIdentity(output);
        var state = GetOwnershipStatePath(workspace.Root);
        using var parsed = JsonDocument.Parse(await File.ReadAllTextAsync(state));
        var outputIdentity = parsed.RootElement.GetProperty("outputIdentity").GetString()!;
        var pending = Path.Combine(workspace.Root, $".lithosharp-ownership-pending-{identity}-{outputIdentity}-{Guid.NewGuid():N}.json");
        File.Copy(state, pending);
        Lsr22RestrictAndCheckPendingOwner(pending);
        var journal = Lsr22WriteJournal(workspace.Root, output, "O", pending, recordedExtended);
        await Lsr22ObserveRecoveryAsync(
            () => GenerateAsync(Lsr22Namespace(output, restartExtended), false, new SingleFileTemplate("current.txt", "current")),
            workspace.Root, output, pending, journal, "ownership", recordedExtended, restartExtended);
        await Assert.That(File.Exists(pending)).IsFalse();
        await Assert.That(GetOwnershipStatePath(workspace.Root)).IsEqualTo(state);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "current.txt"))).IsEqualTo("current");
        using var current = JsonDocument.Parse(await File.ReadAllTextAsync(state));
        await Assert.That(current.RootElement.GetProperty("outputIdentity").GetString()).IsEqualTo(outputIdentity);
        await Assert.That(await File.ReadAllTextAsync(journal)).DoesNotContain("O|");
        await AssertNoTransactionDirectoriesAsync(workspace.Root);
    }

    [Test]
    [Arguments("staging", false, false)]
    [Arguments("staging", false, true)]
    [Arguments("staging", true, false)]
    [Arguments("staging", true, true)]
    [Arguments("backup", false, false)]
    [Arguments("backup", false, true)]
    [Arguments("backup", true, false)]
    [Arguments("backup", true, true)]
    [Arguments("ownership", false, false)]
    [Arguments("ownership", false, true)]
    [Arguments("ownership", true, false)]
    [Arguments("ownership", true, true)]
    public async Task Lsr22_RecoveryRejectsWrongParentOrTamperedName(string kind, bool wrongParent, bool recordedExtended)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        await GenerateAsync(output, true, new SingleFileTemplate("index.html", "initial"));
        var identity = CreateLockIdentity(output);
        var state = GetOwnershipStatePath(workspace.Root);
        using var parsed = JsonDocument.Parse(await File.ReadAllTextAsync(state));
        var outputIdentity = parsed.RootElement.GetProperty("outputIdentity").GetString()!;
        var parent = wrongParent ? Path.Combine(workspace.Root, "outside") : workspace.Root;
        Directory.CreateDirectory(parent);
        var name = kind == "ownership"
            ? $".lithosharp-ownership-pending-{identity}-{outputIdentity}-{Guid.NewGuid():N}.json"
            : $".lithosharp-{kind}-{identity}-{Guid.NewGuid():N}";
        if (!wrongParent) name = "tampered-" + name;
        var registered = Path.Combine(parent, name);
        if (kind == "ownership") File.Copy(state, registered);
        else if (kind == "backup") Directory.Move(output, registered);
        else { Directory.CreateDirectory(registered); await File.WriteAllTextAsync(Path.Combine(registered, "keep.txt"), "untouched"); }
        var journal = Lsr22WriteJournal(workspace.Root, output, kind == "staging" ? "S" : kind == "backup" ? "B" : "O", registered, recordedExtended);
        var beforeJournal = await File.ReadAllBytesAsync(journal);
        var beforeState = await File.ReadAllBytesAsync(state);
        await Assert.That(async () => await Lsr22ObserveRecoveryAsync(
                () => GenerateAsync(Lsr22Namespace(output, !recordedExtended), false, new SingleFileTemplate("current.txt", "current")),
                workspace.Root, output, registered, journal, "reject-" + kind, recordedExtended, !recordedExtended))
            .Throws<InvalidOperationException>();
        await Assert.That(await File.ReadAllBytesAsync(journal)).IsEquivalentTo(beforeJournal);
        await Assert.That(await File.ReadAllBytesAsync(state)).IsEquivalentTo(beforeState);
        if (kind == "ownership") await Assert.That(await File.ReadAllBytesAsync(registered)).IsEquivalentTo(beforeState);
        else await Assert.That(await File.ReadAllTextAsync(Path.Combine(registered, kind == "backup" ? "index.html" : "keep.txt"))).IsEqualTo(kind == "backup" ? "initial" : "untouched");
        await Assert.That(File.Exists(Path.Combine(output, "current.txt"))).IsFalse();
        if (kind != "backup") await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "index.html"))).IsEqualTo("initial");
        else await Assert.That(Directory.Exists(output)).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Lsr22_PendingRecoveryRejectsWrongOutputIdentity(bool recordedExtended)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        await GenerateAsync(output, true, new SingleFileTemplate("index.html", "initial"));
        var identity = CreateLockIdentity(output);
        var state = GetOwnershipStatePath(workspace.Root);
        var original = await File.ReadAllTextAsync(state);
        using var parsed = JsonDocument.Parse(original);
        var outputIdentity = parsed.RootElement.GetProperty("outputIdentity").GetString()!;
        var pending = Path.Combine(workspace.Root, $".lithosharp-ownership-pending-{identity}-{outputIdentity}-{Guid.NewGuid():N}.json");
        var badIdentity = outputIdentity[0] == 'a' ? "b" + outputIdentity[1..] : "a" + outputIdentity[1..];
        await File.WriteAllTextAsync(pending, original.Replace(outputIdentity, badIdentity, StringComparison.Ordinal));
        Lsr22RestrictAndCheckPendingOwner(pending);
        var journal = Lsr22WriteJournal(workspace.Root, output, "O", pending, recordedExtended);
        var pendingBytes = await File.ReadAllBytesAsync(pending);
        await Assert.That(async () => await Lsr22ObserveRecoveryAsync(
                () => GenerateAsync(Lsr22Namespace(output, !recordedExtended), false, new SingleFileTemplate("current.txt", "current")),
                workspace.Root, output, pending, journal, "reject-output-identity", recordedExtended, !recordedExtended))
            .Throws<InvalidOperationException>();
        await Assert.That(await File.ReadAllBytesAsync(pending)).IsEquivalentTo(pendingBytes);
        await Assert.That(await File.ReadAllTextAsync(state)).IsEqualTo(original);
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "index.html"))).IsEqualTo("initial");
        await Assert.That(File.Exists(Path.Combine(output, "current.txt"))).IsFalse();
    }

    private static void Lsr22RestrictAndCheckPendingOwner(string path)
    {
        // Synthetic copied/written pending files do not inherit the protected
        // ownership-state ACL. Use and verify the real transaction predicates
        // before writing the journal or taking recovery evidence snapshots.
        var transaction = typeof(SiteGenerator).GetNestedType(
            "OutputTransaction", System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("OutputTransaction fixture method was not found.");
        foreach (var name in new[] { "RestrictTransactionPathToOwner", "EnsureTransactionPathIsOwnerRestricted" })
        {
            var method = transaction.GetMethod(
                name, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"Transaction fixture method '{name}' was not found.");
            try
            {
                method.Invoke(null, new object[] { path, false });
            }
            catch (System.Reflection.TargetInvocationException error) when (error.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }
    }

    private static string Lsr22Namespace(string path, bool extended)
    {
        var full = Path.GetFullPath(path);
        if (!extended) return full;
        return full.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + full[2..]
            : @"\\?\" + full;
    }

    private static string Lsr22WriteJournal(string parent, string output, string operation, string registered, bool extended)
    {
        var journal = Path.Combine(parent, $".lithosharp-lock-{CreateLockIdentity(output)}.lock");
        // Scope is already the canonical ordinary ownership identity. Only the
        // recorded I/O path preserves the interrupted process's namespace.
        File.WriteAllText(journal, operation + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(Path.GetFullPath(output)))
            + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(Lsr22Namespace(registered, extended))) + "\n");
        return journal;
    }

    private static async Task Lsr22ObserveRecoveryAsync(Func<Task> action, string root, string output, string registered, string journal, string kind, bool recordedExtended, bool restartExtended)
    {
        async Task Snapshot(string phase, Exception? failure)
        {
            async Task<object> Tree(string path)
            {
                if (File.Exists(path)) return new { exists = true, file = true, sha256 = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path))) };
                if (!Directory.Exists(path)) return new { exists = false };
                var rows = new List<object>();
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                    rows.Add(new { relative = Path.GetRelativePath(path, file), sha256 = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(file))) });
                return new { exists = true, file = false, files = rows };
            }
            Console.WriteLine("LSR_RECOVERY " + JsonSerializer.Serialize(new
            {
                kind, phase, recordedExtended, restartExtended, error = failure?.GetType().FullName,
                output = await Tree(output), registered = await Tree(registered), journal = await Tree(journal),
                staging = FindTransactionDirectories(root, "staging").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray(),
                backup = FindTransactionDirectories(root, "backup").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()
            }));
        }
        await Snapshot("before-recovery", null);
        Exception? primary = null;
        try { await action(); } catch (Exception exception) { primary = exception; }
        try { await Snapshot("after-recovery-before-assertions", primary); }
        catch (Exception secondary) { if (primary is not null) throw new AggregateException(primary, secondary); throw; }
        if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
    }

    [Test]
    [SupportedOSPlatform("windows")]
    public async Task GenerateAsync_CleanFalsePreservesWindowsSecurityDescriptors()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        var directory = Path.Combine(output, "preserved");
        var file = Path.Combine(directory, "page.html");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(file, "old");
        ApplyDistinctWindowsAcl(output, isDirectory: true);
        ApplyDistinctWindowsAcl(directory, isDirectory: true);
        ApplyDistinctWindowsAcl(file, isDirectory: false);
        var expectedRoot = GetWindowsSecurityDescriptor(output, isDirectory: true);
        var expectedDirectory = GetWindowsSecurityDescriptor(directory, isDirectory: true);
        var expectedFile = GetWindowsSecurityDescriptor(file, isDirectory: false);

        await GenerateAsync(
            output,
            clean: false,
            new SingleFileTemplate("preserved/page.html", "current"));

        await Assert.That(GetWindowsSecurityDescriptor(output, isDirectory: true))
            .IsEqualTo(expectedRoot);
        await Assert.That(GetWindowsSecurityDescriptor(directory, isDirectory: true))
            .IsEqualTo(expectedDirectory);
        await Assert.That(GetWindowsSecurityDescriptor(file, isDirectory: false))
            .IsEqualTo(expectedFile);

        var lockPath = Path.Combine(
            workspace.Root,
            $".lithosharp-lock-{CreateLockIdentity(output)}.lock");
        var lockSecurity = new FileInfo(lockPath)
            .GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        await Assert.That(lockSecurity.AreAccessRulesProtected).IsTrue();
        var owner = WindowsIdentity.GetCurrent().User!;
        await Assert.That(owner.Equals(lockSecurity.GetOwner(typeof(SecurityIdentifier)))).IsTrue();
        var lockRules = lockSecurity.GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        await Assert.That(lockRules).IsNotEmpty();
        await Assert.That(lockRules.All(rule =>
            rule.AccessControlType == AccessControlType.Allow
            && owner.Equals(rule.IdentityReference))).IsTrue();

        var stateSecurity = new FileInfo(GetOwnershipStatePath(workspace.Root))
            .GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        await Assert.That(stateSecurity.AreAccessRulesProtected).IsTrue();
        await Assert.That(owner.Equals(stateSecurity.GetOwner(typeof(SecurityIdentifier)))).IsTrue();
        var stateRules = stateSecurity.GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        await Assert.That(stateRules).IsNotEmpty();
        await Assert.That(stateRules.All(rule =>
            rule.AccessControlType == AccessControlType.Allow
            && owner.Equals(rule.IdentityReference))).IsTrue();
    }

    [Test]
    public async Task GenerateAsync_ReadOnlyBackupIsCleanedAfterCommittedGeneration()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TemporaryWorkspace();
        var output = Path.Combine(workspace.Root, "output");
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "old.txt"), "old");
        File.SetUnixFileMode(
            output,
            UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var result = await GenerateAsync(
                output,
                clean: true,
                new SingleFileTemplate("index.html", "current"));

            await Assert.That(result.GeneratedFiles).Contains(Path.Combine(output, "index.html"));
            await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "index.html")))
                .IsEqualTo("current");
            // Owned backup cleanup restores owner permissions before deletion.
            await Assert.That(result.BuildReport.RetainedRecoveryState).IsFalse();
            await Assert.That(result.BuildReport.Diagnostics).IsEmpty();
            await Assert.That(FindTransactionDirectories(workspace.Root, "backup")).IsEmpty();
            await GenerateAsync(
                output,
                clean: false,
                new SingleFileTemplate("second.html", "second"));
            await Assert.That(FindTransactionDirectories(workspace.Root, "backup")).IsEmpty();
        }
        finally
        {
            foreach (var directory in FindTransactionDirectories(workspace.Root, "backup"))
            {
                File.SetUnixFileMode(
                    directory,
                    UnixFileMode.UserRead
                    | UnixFileMode.UserWrite
                    | UnixFileMode.UserExecute);
            }

            if (Directory.Exists(output))
            {
                File.SetUnixFileMode(
                    output,
                    UnixFileMode.UserRead
                    | UnixFileMode.UserWrite
                    | UnixFileMode.UserExecute);
            }
        }
    }

    private static Task<SiteGenerationResult> GenerateAsync(
        string output,
        bool clean,
        ISiteTemplate template,
        CancellationToken cancellationToken = default) =>
        new SiteGenerator().GenerateWithOptionsAsync(
            new SiteSettings
            {
                Title = "Test Site",
                Description = "A test site.",
                BaseUrl = "https://example.test/",
                Language = "en",
                TimeZone = "UTC"
            },
            [],
            output,
            clean,
            new SiteCustomization { Template = template },
            new SiteGenerationOptions
            {
                BuildTimestamp = new DateTimeOffset(
                    2026,
                    1,
                    2,
                    3,
                    4,
                    5,
                    TimeSpan.Zero)
            },
            cancellationToken);

    private static bool TryCreateDirectorySymbolicLink(string path, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(path, target);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static string CreateLockIdentity(string output)
    {
        var normalizedOutput = Path.TrimEndingDirectorySeparator(Path.GetFullPath(output))
            .Normalize(NormalizationForm.FormC)
            .ToUpperInvariant();
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(normalizedOutput)))
            .ToLowerInvariant();
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        var startedAt = Stopwatch.GetTimestamp();
        while (!File.Exists(path))
        {
            if (Stopwatch.GetElapsedTime(startedAt) >= timeout)
            {
                throw new TimeoutException($"Timed out waiting for '{path}'.");
            }

            await Task.Delay(20);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ApplyDistinctWindowsAcl(string path, bool isDirectory)
    {
        var owner = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException(
                "The current Windows identity has no security identifier.");
        var world = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        FileSystemSecurity security = isDirectory
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Access);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            owner,
            FileSystemRights.FullControl,
            isDirectory
                ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
                : InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            world,
            FileSystemRights.ReadAndExecute,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));
        if (isDirectory)
        {
            new DirectoryInfo(path).SetAccessControl((DirectorySecurity)security);
        }
        else
        {
            new FileInfo(path).SetAccessControl((FileSecurity)security);
        }
    }

    [SupportedOSPlatform("windows")]
    private static string GetWindowsSecurityDescriptor(string path, bool isDirectory)
    {
        const AccessControlSections sections =
            AccessControlSections.Access
            | AccessControlSections.Owner
            | AccessControlSections.Group;
        FileSystemSecurity security = isDirectory
            ? new DirectoryInfo(path).GetAccessControl(sections)
            : new FileInfo(path).GetAccessControl(sections);
        return security.GetSecurityDescriptorSddlForm(sections);
    }

    private static async Task AssertNoTransactionDirectoriesAsync(string parent)
    {
        var leftovers = FindTransactionDirectories(parent, "staging")
            .Concat(FindTransactionDirectories(parent, "backup"))
            .ToArray();
        await Assert.That(leftovers).IsEmpty();
    }

    private static string[] FindTransactionDirectories(string parent, string kind) =>
        Directory.EnumerateDirectories(parent)
            .Where(path => Path.GetFileName(path)
                .Contains($".lithosharp-{kind}-", StringComparison.Ordinal))
            .ToArray();

    private static string GetOwnershipStatePath(string parent) =>
        Directory.EnumerateFiles(parent, ".lithosharp-ownership-*.json")
            .Single(path => !Path.GetFileName(path)
                .StartsWith(
                    ".lithosharp-ownership-pending-",
                    StringComparison.Ordinal));

    private sealed class SingleFileTemplate(
        string relativePath,
        string content,
        bool observeCancellation = true) : ISiteTemplate
    {
        public Task<SiteTemplateResult> RenderAsync(
            SiteTemplateContext context,
            CancellationToken cancellationToken = default)
        {
            if (observeCancellation)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            return Task.FromResult(new SiteTemplateResult(
            [
                new SiteTemplateFile
                {
                    RelativePath = relativePath,
                    Content = content
                }
            ]));
        }
    }

    private sealed class GatedTemplate(
        string relativePath,
        string content,
        CountdownEvent ready,
        Task release) : ISiteTemplate
    {
        public async Task<SiteTemplateResult> RenderAsync(
            SiteTemplateContext context,
            CancellationToken cancellationToken = default)
        {
            ready.Signal();
            await release.WaitAsync(cancellationToken);
            return new SiteTemplateResult(
            [
                new SiteTemplateFile
                {
                    RelativePath = relativePath,
                    Content = content
                }
            ]);
        }
    }
}
