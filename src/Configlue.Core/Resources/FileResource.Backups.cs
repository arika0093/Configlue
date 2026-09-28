using System.Diagnostics;
using System.Security.Cryptography;

namespace Configlue.Resources;

public sealed partial class FileResource
{
    private async ValueTask CreateBackupAsync(
        byte[] previousContent,
        CancellationToken cancellationToken
    )
    {
        Directory.CreateDirectory(_backupDirectory);
        SetHiddenOnWindows(_backupDirectory);
        var existingBackupPaths = GetExistingBackupPaths();
        var retainedPreviousCount = Math.Min(
            _options.BackupMaxCount - 1,
            existingBackupPaths.Count
        );
        var stagedBackupPaths = new string?[retainedPreviousCount];
        try
        {
            for (var index = 0; index < retainedPreviousCount; index++)
            {
                if (
                    TryGetCurrentBackupIndex(existingBackupPaths[index], out var currentIndex)
                    && currentIndex > index + 1
                    && currentIndex <= retainedPreviousCount
                )
                {
                    var stagedPath = System.IO.Path.Combine(
                        _backupDirectory,
                        ".configlue-backup-stage-" + Guid.NewGuid().ToString("N")
                    );
                    stagedBackupPaths[index] = stagedPath;
                    File.Copy(existingBackupPaths[index], stagedPath);
                }
            }

            for (var index = retainedPreviousCount; index > 0; index--)
            {
                var previousBackupPath =
                    stagedBackupPaths[index - 1] ?? existingBackupPaths[index - 1];
                var olderContent = await File.ReadAllBytesAsync(
                        previousBackupPath,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                var backupPath = GetBackupPath(index);
                await WriteAtomicAsync(backupPath, olderContent, cancellationToken)
                    .ConfigureAwait(false);
                SetHiddenOnWindows(backupPath);
            }

            var latestBackupPath = GetBackupPath(0);
            await WriteAtomicAsync(latestBackupPath, previousContent, cancellationToken)
                .ConfigureAwait(false);
            SetHiddenOnWindows(latestBackupPath);

            foreach (var existingBackupPath in existingBackupPaths)
            {
                if (
                    !IsInCurrentBackupDirectory(existingBackupPath)
                    || !TryGetCurrentBackupIndex(existingBackupPath, out var existingIndex)
                    || existingIndex > retainedPreviousCount
                )
                {
                    File.Delete(existingBackupPath);
                }
            }
        }
        finally
        {
            foreach (var stagedPath in stagedBackupPaths.Where(static path => path is not null))
            {
                try
                {
                    File.Delete(stagedPath!);
                }
                catch (IOException)
                {
                    // A failed cleanup does not affect the retained backups.
                }
            }
        }
    }

    private string? GetLatestBackupPath()
    {
        var backupPaths = GetExistingBackupPaths();
        return backupPaths.Count == 0 ? null : backupPaths[0];
    }

    private List<string> GetExistingBackupPaths()
    {
        var backupPaths = new List<string>();
        var currentBackups = new List<(int Index, int DirectoryPriority, string Path)>();
        var legacyBackups = new List<FileInfo>();
        for (
            var directoryIndex = 0;
            directoryIndex <= _previousBackupDirectories.Length;
            directoryIndex++
        )
        {
            var directory =
                directoryIndex == 0
                    ? _backupDirectory
                    : _previousBackupDirectories[directoryIndex - 1];
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(directory))
            {
                if (TryGetCurrentBackupIndex(path, out var index))
                {
                    currentBackups.Add((index, directoryIndex, path));
                }
            }

            var legacyPrefix = System.IO.Path.GetFileNameWithoutExtension(_path);
            if (!OperatingSystem.IsWindows())
            {
                legacyPrefix = "." + legacyPrefix;
            }

            var legacyPattern = legacyPrefix + "_*" + System.IO.Path.GetExtension(_path) + ".bak";
            foreach (var path in Directory.EnumerateFiles(directory, legacyPattern))
            {
                if (
                    directoryIndex == 0
                    && !string.Equals(_backupFileName, _fileName, StringComparison.Ordinal)
                )
                {
                    continue;
                }

                legacyBackups.Add(new FileInfo(path));
            }
        }

        currentBackups.Sort(
            static (left, right) =>
            {
                var indexComparison = left.Index.CompareTo(right.Index);
                return indexComparison != 0
                    ? indexComparison
                    : left.DirectoryPriority.CompareTo(right.DirectoryPriority);
            }
        );
        foreach (var backup in currentBackups)
        {
            backupPaths.Add(backup.Path);
        }

        legacyBackups.Sort(
            static (left, right) => right.CreationTimeUtc.CompareTo(left.CreationTimeUtc)
        );
        foreach (var backup in legacyBackups)
        {
            backupPaths.Add(backup.FullName);
        }

        return backupPaths;
    }

    private bool IsInCurrentBackupDirectory(string path) =>
        string.Equals(
            System.IO.Path.GetDirectoryName(path),
            _backupDirectory,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal
        );

    private bool TryGetCurrentBackupIndex(string path, out int index)
    {
        var fileName = System.IO.Path.GetFileName(path);
        if (
            TryGetBackupIndex(fileName, _backupFileName + _options.BackupExtension, out index)
            || (
                !string.Equals(_backupFileName, _fileName, StringComparison.Ordinal)
                && !IsInCurrentBackupDirectory(path)
                && TryGetBackupIndex(fileName, _fileName + _options.BackupExtension, out index)
            )
        )
        {
            return true;
        }

        index = -1;
        return false;
    }

    private static bool TryGetBackupIndex(string fileName, string baseName, out int index)
    {
        if (string.Equals(fileName, baseName, StringComparison.Ordinal))
        {
            index = 0;
            return true;
        }

        var indexPrefix = baseName + ".";
        if (
            fileName.StartsWith(indexPrefix, StringComparison.Ordinal)
            && int.TryParse(
                fileName.AsSpan(indexPrefix.Length),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out index
            )
            && index > 0
        )
        {
            return true;
        }

        index = -1;
        return false;
    }

    private static void SetHiddenOnWindows(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
        }
    }

    private string GetBackupPath(int index)
    {
        var backupName = _backupFileName + _options.BackupExtension;
        if (index > 0)
        {
            backupName += "." + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return System.IO.Path.Combine(_backupDirectory, backupName);
    }
}
