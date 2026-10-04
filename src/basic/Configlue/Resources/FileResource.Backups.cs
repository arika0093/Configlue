namespace Configlue.Resources;

public sealed partial class FileResource
{
    private async ValueTask CreateBackupAsync(
        byte[] previousContent,
        CancellationToken cancellationToken
    )
    {
        var backupDirectory = System.IO.Path.GetDirectoryName(_backupPath)!;
        Directory.CreateDirectory(backupDirectory);
        await WriteAtomicAsync(_backupPath, previousContent, cancellationToken)
            .ConfigureAwait(false);
    }

    private string? GetLatestBackupPath() => File.Exists(_backupPath) ? _backupPath : null;
}
