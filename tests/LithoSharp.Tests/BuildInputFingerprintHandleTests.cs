using LithoSharp.Build;

namespace LithoSharp.Tests;

public sealed class BuildInputFingerprintHandleTests
{
    [Test]
    public void VerifyOpenedContainedFile_AcceptsAWriteOnlyCreatedFile()
    {
        using var workspace = new TemporaryWorkspace();
        var path = Path.Combine(workspace.Root, "new.txt");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        BuildInputFingerprint.VerifyOpenedContainedFile(workspace.Root, path, stream.SafeFileHandle);
    }

    [Test]
    public async Task VerifyOpenedContainedFile_RejectsAnOpenedFileOutsideTheExpectedRoot()
    {
        using var workspace = new TemporaryWorkspace();
        var root = Path.Combine(workspace.Root, "allowed");
        Directory.CreateDirectory(root);
        var outside = Path.Combine(workspace.Root, "outside.txt");
        await File.WriteAllTextAsync(outside, "sentinel");
        using var stream = File.OpenRead(outside);
        await Assert.That(() => BuildInputFingerprint.VerifyOpenedContainedFile(
            root, Path.Combine(root, "expected.txt"), stream.SafeFileHandle))
            .Throws<InvalidOperationException>();
        await Assert.That(await File.ReadAllTextAsync(outside)).IsEqualTo("sentinel");
    }

    [Test]
    public async Task VerifyOpenedContainedFile_RejectsADifferentFileWithinTheRoot()
    {
        using var workspace = new TemporaryWorkspace();
        var actual = Path.Combine(workspace.Root, "actual.txt");
        await File.WriteAllTextAsync(actual, "sentinel");
        using var stream = File.OpenRead(actual);
        await Assert.That(() => BuildInputFingerprint.VerifyOpenedContainedFile(
            workspace.Root, Path.Combine(workspace.Root, "expected.txt"), stream.SafeFileHandle))
            .Throws<InvalidOperationException>();
    }
}
