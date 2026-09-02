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
        var script = $$"""
            $stream = [System.IO.FileStream]::new(
                '{{lockPath.Replace("'", "''", StringComparison.Ordinal)}}',
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
            await WaitForFileAsync(readyPath, TimeSpan.FromSeconds(5));
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
            .GetAccessControl(AccessControlSections.Access);
        await Assert.That(lockSecurity.AreAccessRulesProtected).IsTrue();
        var owner = WindowsIdentity.GetCurrent().User!;
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
            .GetAccessControl(AccessControlSections.Access);
        await Assert.That(stateSecurity.AreAccessRulesProtected).IsTrue();
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
    public async Task GenerateAsync_BackupCleanupFailureDoesNotFailCommittedGeneration()
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
        using var messages = new StringWriter();
        using var listener = new TextWriterTraceListener(messages);
        Trace.Listeners.Add(listener);
        try
        {
            var result = await GenerateAsync(
                output,
                clean: true,
                new SingleFileTemplate("index.html", "current"));

            listener.Flush();
            await Assert.That(result.GeneratedFiles).Contains(Path.Combine(output, "index.html"));
            await Assert.That(await File.ReadAllTextAsync(Path.Combine(output, "index.html")))
                .IsEqualTo("current");
            await Assert.That(result.BuildReport.RetainedRecoveryState).IsTrue();
            var diagnostic = result.BuildReport.Diagnostics.Single();
            await Assert.That(diagnostic.Id).IsEqualTo("LST001");
            await Assert.That(diagnostic.Severity).IsEqualTo(SiteDiagnosticSeverity.Warning);
            await Assert.That(diagnostic.Message)
                .IsEqualTo("Committed output retained a backup for a later recovery cleanup.");
            await Assert.That(diagnostic.Message).DoesNotContain(workspace.Root);
            await Assert.That(diagnostic.Location).IsNull();
            await Assert.That(messages.ToString()).Contains("retained backup");
            await Assert.That(FindTransactionDirectories(workspace.Root, "backup")).Count().IsEqualTo(1);

            var retainedBackup = FindTransactionDirectories(workspace.Root, "backup").Single();
            File.SetUnixFileMode(
                retainedBackup,
                UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute);
            await GenerateAsync(
                output,
                clean: false,
                new SingleFileTemplate("second.html", "second"));
            await Assert.That(Directory.Exists(retainedBackup)).IsFalse();
            await Assert.That(FindTransactionDirectories(workspace.Root, "backup")).IsEmpty();
        }
        finally
        {
            Trace.Listeners.Remove(listener);
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
