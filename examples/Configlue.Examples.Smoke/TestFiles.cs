namespace Configlue.Examples.Smoke;

// Isolated temporary directory helper for file-backed smoke tests.
internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        FullPath = Path.Combine(
            Path.GetTempPath(),
            "Configlue.Examples.Smoke",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(FullPath);
    }

    public string FullPath { get; }

    public void Dispose()
    {
        if (Directory.Exists(FullPath))
        {
            Directory.Delete(FullPath, recursive: true);
        }
    }
}
