namespace PageSharp.Tests;

internal sealed class TemporaryWorkspace : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "pagesharp-tests", Guid.NewGuid().ToString("N"));

    public TemporaryWorkspace()
    {
        Directory.CreateDirectory(Root);
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
