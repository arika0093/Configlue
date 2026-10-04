namespace Configlue.Resources;

public sealed partial class FileResource
{
    /// <summary>Copies the replaced content to the single backup path.</summary>
    /// <remarks>Only the most recently replaced content is retained; the backup is not a history and
    /// cross-process concurrent writes may interleave backup and main-file replacement.</remarks>
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
