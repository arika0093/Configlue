namespace Configlue.Resources;

public sealed partial class FileResource
{
    private const int MaxDirectBackupProbeCount = 256;

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
            StageOverwrittenBackups(existingBackupPaths, retainedPreviousCount, stagedBackupPaths);

            for (var index = retainedPreviousCount; index > 0; index--)
            {
                var stagedPath = stagedBackupPaths[index - 1];
                var backupPath = GetBackupPath(index);
                if (stagedPath is not null)
                {
                    var stagedContent = await File.ReadAllBytesAsync(stagedPath, cancellationToken)
                        .ConfigureAwait(false);
                    await WriteAtomicAsync(backupPath, stagedContent, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    var sourcePath = existingBackupPaths[index - 1];
                    if (
                        !PathsEqual(sourcePath, backupPath)
                        && !TryRelocateBackupFile(sourcePath, backupPath)
                    )
                    {
                        var olderContent = await File.ReadAllBytesAsync(
                                sourcePath,
                                cancellationToken
                            )
                            .ConfigureAwait(false);
                        await WriteAtomicAsync(backupPath, olderContent, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                SetHiddenOnWindows(backupPath);
            }

            var latestBackupPath = GetBackupPath(0);
            await WriteAtomicAsync(latestBackupPath, previousContent, cancellationToken)
                .ConfigureAwait(false);
            SetHiddenOnWindows(latestBackupPath);

            for (var index = 0; index < existingBackupPaths.Count; index++)
            {
                var existingBackupPath = existingBackupPaths[index];
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
            for (var index = 0; index < stagedBackupPaths.Length; index++)
            {
                var stagedPath = stagedBackupPaths[index];
                if (stagedPath is null)
                {
                    continue;
                }

                try
                {
                    File.Delete(stagedPath);
                }
                catch (IOException)
                {
                    // A failed cleanup does not affect the retained backups.
                }
            }
        }
    }

    private void StageOverwrittenBackups(
        List<string> existingBackupPaths,
        int retainedPreviousCount,
        string?[] stagedBackupPaths
    )
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
    }

    private string? GetLatestBackupPath()
    {
        var backupPaths = GetExistingBackupPaths();
        return backupPaths.Count == 0 ? null : backupPaths[0];
    }

    private List<string> GetExistingBackupPaths()
    {
        if (RequiresFullBackupEnumeration())
        {
            return GetExistingBackupPathsByEnumeration();
        }

        var currentBackups = new List<CurrentBackup>();
        for (
            var directoryIndex = 0;
            directoryIndex <= _previousBackupDirectories.Length;
            directoryIndex++
        )
        {
            var directory = GetBackupDirectory(directoryIndex);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            var includeFallbackName =
                directoryIndex != 0
                && !string.Equals(_backupFileName, _fileName, StringComparison.Ordinal);
            for (var index = 0; index < _options.BackupMaxCount; index++)
            {
                AddExistingBackup(
                    currentBackups,
                    directory,
                    index,
                    directoryIndex,
                    _backupFileName
                );
                if (includeFallbackName)
                {
                    AddExistingBackup(currentBackups, directory, index, directoryIndex, _fileName);
                }
            }
        }

        currentBackups.Sort(CompareCurrentBackups);
        var backupPaths = new List<string>(currentBackups.Count);
        for (var index = 0; index < currentBackups.Count; index++)
        {
            backupPaths.Add(currentBackups[index].Path);
        }

        return backupPaths;
    }

    private void AddExistingBackup(
        List<CurrentBackup> currentBackups,
        string directory,
        int index,
        int directoryPriority,
        string baseName
    )
    {
        var path = GetBackupPath(directory, index, baseName);
        if (File.Exists(path))
        {
            currentBackups.Add(new CurrentBackup(index, directoryPriority, path));
        }
    }

    private List<string> GetExistingBackupPathsByEnumeration()
    {
        var backupPaths = new List<string>();
        var currentBackups = new List<CurrentBackup>();
        var legacyBackups = new List<LegacyBackup>();
        for (
            var directoryIndex = 0;
            directoryIndex <= _previousBackupDirectories.Length;
            directoryIndex++
        )
        {
            var directory = GetBackupDirectory(directoryIndex);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(directory))
            {
                if (TryGetCurrentBackupIndex(path, out var index))
                {
                    currentBackups.Add(new CurrentBackup(index, directoryIndex, path));
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

                legacyBackups.Add(new LegacyBackup(File.GetCreationTimeUtc(path), path));
            }
        }

        currentBackups.Sort(CompareCurrentBackups);
        for (var index = 0; index < currentBackups.Count; index++)
        {
            backupPaths.Add(currentBackups[index].Path);
        }

        legacyBackups.Sort(
            static (left, right) => right.CreationTimeUtc.CompareTo(left.CreationTimeUtc)
        );
        for (var index = 0; index < legacyBackups.Count; index++)
        {
            backupPaths.Add(legacyBackups[index].Path);
        }

        return backupPaths;
    }

    private string GetBackupDirectory(int directoryIndex) =>
        directoryIndex == 0 ? _backupDirectory : _previousBackupDirectories[directoryIndex - 1];

    private bool RequiresFullBackupEnumeration()
    {
        if (_options.BackupMaxCount > MaxDirectBackupProbeCount)
        {
            return true;
        }

        return HasLegacyBackups() || HasBackupBeyondConfiguredRange();
    }

    private bool HasLegacyBackups()
    {
        var legacyPrefix = System.IO.Path.GetFileNameWithoutExtension(_path);
        if (!OperatingSystem.IsWindows())
        {
            legacyPrefix = "." + legacyPrefix;
        }

        var legacyPattern = legacyPrefix + "_*" + System.IO.Path.GetExtension(_path) + ".bak";
        for (
            var directoryIndex = 0;
            directoryIndex <= _previousBackupDirectories.Length;
            directoryIndex++
        )
        {
            var directory = GetBackupDirectory(directoryIndex);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            if (
                directoryIndex == 0
                && !string.Equals(_backupFileName, _fileName, StringComparison.Ordinal)
            )
            {
                continue;
            }

            using var enumerator = Directory
                .EnumerateFiles(directory, legacyPattern)
                .GetEnumerator();
            if (enumerator.MoveNext())
            {
                return true;
            }
        }

        return false;
    }

    private bool HasBackupBeyondConfiguredRange()
    {
        var probeIndex = _options.BackupMaxCount;
        for (
            var directoryIndex = 0;
            directoryIndex <= _previousBackupDirectories.Length;
            directoryIndex++
        )
        {
            var directory = GetBackupDirectory(directoryIndex);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            if (File.Exists(GetBackupPath(directory, probeIndex, _backupFileName)))
            {
                return true;
            }

            if (
                directoryIndex != 0
                && !string.Equals(_backupFileName, _fileName, StringComparison.Ordinal)
                && File.Exists(GetBackupPath(directory, probeIndex, _fileName))
            )
            {
                return true;
            }
        }

        return false;
    }

    private static int CompareCurrentBackups(CurrentBackup left, CurrentBackup right)
    {
        var indexComparison = left.Index.CompareTo(right.Index);
        return indexComparison != 0
            ? indexComparison
            : left.DirectoryPriority.CompareTo(right.DirectoryPriority);
    }

    private static bool TryRelocateBackupFile(string sourcePath, string destinationPath)
    {
        try
        {
            File.Move(sourcePath, destinationPath, overwrite: true);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            left,
            right,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal
        );

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
#if NETSTANDARD2_0
                fileName.Substring(indexPrefix.Length),
#else
                fileName.AsSpan(indexPrefix.Length),
#endif
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

    private string GetBackupPath(int index) =>
        GetBackupPath(_backupDirectory, index, _backupFileName);

    private string GetBackupPath(string directory, int index, string baseName)
    {
        var backupName = baseName + _options.BackupExtension;
        if (index > 0)
        {
            backupName += "." + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return System.IO.Path.Combine(directory, backupName);
    }

    private readonly record struct CurrentBackup
    {
        public int Index { get; init; }
        public int DirectoryPriority { get; init; }
        public string Path { get; init; }

        public CurrentBackup(int index, int directoryPriority, string path)
        {
            Index = index;
            DirectoryPriority = directoryPriority;
            Path = path;
        }
    }

    private readonly record struct LegacyBackup
    {
        public DateTime CreationTimeUtc { get; init; }
        public string Path { get; init; }

        public LegacyBackup(DateTime creationTimeUtc, string path)
        {
            CreationTimeUtc = creationTimeUtc;
            Path = path;
        }
    }
}
