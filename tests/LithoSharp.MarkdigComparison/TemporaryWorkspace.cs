namespace LithoSharp.Tests;

internal sealed class TemporaryWorkspace : IDisposable
{
    public string Root { get; } = Path.Combine(AppContext.BaseDirectory, $"lithosharp-test-{Guid.NewGuid():N}");

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
