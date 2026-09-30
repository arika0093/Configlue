using System.Diagnostics;
using System.Runtime.InteropServices;
using Configlue.Codecs;
using Configlue.State;

namespace Configlue.Resources;

public sealed partial class FileResource
{
    /// <inheritdoc />
    public ValueTask<ResourceReadResult> ReadAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    ) => ReadAsync(cancellationToken);

    /// <inheritdoc />
    public async ValueTask<ResourceReadResult> ReadAsync(
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var content = await File.ReadAllBytesAsync(_path, cancellationToken)
                .ConfigureAwait(false);
            return ResourceReadResult.Success(content, GetRevision(content));
        }
        catch (FileNotFoundException)
        {
            return ResourceReadResult.NotFound();
        }
        catch (DirectoryNotFoundException)
        {
            return ResourceReadResult.NotFound();
        }
        catch (IOException)
        {
            return ResourceReadResult.Unavailable();
        }
    }

    /// <inheritdoc />
    public ValueTask<StateWriteResult> WriteAsync(
        ConfiglueResourceContext context,
        ResourceWriteRequest request,
        CancellationToken cancellationToken = default
    ) => WriteAsync(request, cancellationToken);

    /// <inheritdoc />
    public ValueTask<StateWriteResult> WriteAsync(
        ResourceWriteRequest request,
        CancellationToken cancellationToken = default
    ) => WriteBatchAsync([ResourceWriteMutation.Replace(request)], cancellationToken);

    /// <inheritdoc />
    public async ValueTask<StateWriteResult> WriteBatchAsync(
        IReadOnlyList<ResourceWriteMutation> mutations,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        ResourceWriteMutation.ValidateBatch(mutations);
        Directory.CreateDirectory(_directory);

        var processLock = await AcquireProcessLockAsync(_path, cancellationToken)
            .ConfigureAwait(false);
        try
        {
#if NETSTANDARD
            using var interprocessLock = await AcquireInterprocessLockAsync(cancellationToken)
                .ConfigureAwait(false);
#else
            await using var interprocessLock = await AcquireInterprocessLockAsync(cancellationToken)
                .ConfigureAwait(false);
#endif
            var checkRevision = mutations.Any(static mutation => !mutation.Condition.IsNone);
            var canSkipRead =
                !checkRevision
                && !(_options.CreateBackup && _options.BackupMaxCount > 0)
                && mutations.Count == 1
                && mutations[0].TryGetOwnedReplacementContent(out _);
            var previousContent = canSkipRead
                ? null
                : await TryReadForWriteAsync(cancellationToken).ConfigureAwait(false);
            var currentRevision = previousContent is null ? null : GetRevision(previousContent);
            if (!mutations[0].Condition.IsSatisfiedBy(currentRevision, previousContent is not null))
            {
                throw new StateConflictException(
                    $"The file resource '{_path}' changed after it was read."
                );
            }

            var content = ApplyMutations(mutations, previousContent, currentRevision);
            cancellationToken.ThrowIfCancellationRequested();
            if (_options.CreateBackup && _options.BackupMaxCount > 0 && previousContent is not null)
            {
                await CreateBackupAsync(previousContent, cancellationToken).ConfigureAwait(false);
            }

            await WriteAtomicAsync(_path, content, cancellationToken).ConfigureAwait(false);
            return new StateWriteResult(GetRevision(content.Span));
        }
        finally
        {
            processLock.Dispose();
        }
    }

    private static ReadOnlyMemory<byte> ApplyMutations(
        IReadOnlyList<ResourceWriteMutation> mutations,
        byte[]? previousContent,
        string? revision
    )
    {
        if (
            mutations.Count == 1
            && mutations[0].TryGetOwnedReplacementContent(out var replacementContent)
            && MemoryMarshal.TryGetArray(replacementContent, out var replacementSegment)
            && replacementSegment.Array is byte[] replacementArray
            && replacementSegment.Offset == 0
            && replacementSegment.Count == replacementArray.Length
        )
        {
            return replacementArray;
        }

        var current = previousContent is null
            ? ResourceReadResult.NotFound(revision)
            : ResourceReadResult.Success(previousContent, revision);
        if (mutations.Count == 1)
        {
            var mutation = mutations[0];
            var singleContent = mutation.Apply(current);
            return mutation.HasStableContent ? singleContent : singleContent.ToArray();
        }

        var content = mutations[0].Apply(current).ToArray();
        current = ResourceReadResult.Success(content, revision);
        for (var index = 1; index < mutations.Count; index++)
        {
            content = mutations[index].Apply(current).ToArray();
            current = ResourceReadResult.Success(content, revision);
        }

        return content;
    }

    /// <summary>Restores the latest backup without creating another backup generation.</summary>
    /// <exception cref="FileNotFoundException">No latest backup exists.</exception>
    public async ValueTask<StateWriteResult> RestoreLatestBackupAsync(
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_directory);
        var processLock = await AcquireProcessLockAsync(_path, cancellationToken)
            .ConfigureAwait(false);
        try
        {
#if NETSTANDARD
            using var interprocessLock = await AcquireInterprocessLockAsync(cancellationToken)
                .ConfigureAwait(false);
#else
            await using var interprocessLock = await AcquireInterprocessLockAsync(cancellationToken)
                .ConfigureAwait(false);
#endif
            var backupPath = GetLatestBackupPath();
            if (backupPath is null)
            {
                throw new FileNotFoundException(
                    "No backup exists for this file resource.",
                    GetBackupPath(0)
                );
            }

            var content = await File.ReadAllBytesAsync(backupPath, cancellationToken)
                .ConfigureAwait(false);
            await WriteAtomicAsync(_path, content, cancellationToken).ConfigureAwait(false);
            return new StateWriteResult(GetRevision(content));
        }
        finally
        {
            processLock.Dispose();
        }
    }

    /// <inheritdoc />
    public async ValueTask<ResourceReadResult?> TryRecoverLatestBackupAsync(
        string? expectedRevision,
        bool expectedMissing,
        Func<ResourceReadResult, CancellationToken, ValueTask<bool>> validate,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(validate);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_directory);
        var processLock = await AcquireProcessLockAsync(_path, cancellationToken)
            .ConfigureAwait(false);
        try
        {
#if NETSTANDARD
            using var interprocessLock = await AcquireInterprocessLockAsync(cancellationToken)
                .ConfigureAwait(false);
#else
            await using var interprocessLock = await AcquireInterprocessLockAsync(cancellationToken)
                .ConfigureAwait(false);
#endif
            var current = await TryReadForWriteAsync(cancellationToken).ConfigureAwait(false);
            var currentRevision = current is null ? null : GetRevision(current);
            if (
                expectedMissing != (current is null)
                || !string.Equals(expectedRevision, currentRevision, StringComparison.Ordinal)
            )
            {
                throw new StateConflictException(
                    $"The file resource '{_path}' changed while backup recovery was being prepared."
                );
            }

            var backupPath = GetLatestBackupPath();
            if (backupPath is null)
            {
                return null;
            }

            byte[] backup;
            try
            {
                backup = await File.ReadAllBytesAsync(backupPath, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }

            var backupResult = ResourceReadResult.Success(backup, GetRevision(backup));
            if (!await validate(backupResult, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            await WriteAtomicAsync(_path, backup, cancellationToken).ConfigureAwait(false);
            return backupResult;
        }
        finally
        {
            processLock.Dispose();
        }
    }

    private async ValueTask<byte[]?> TryReadForWriteAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static void MoveReplacing(string sourcePath, string destinationPath)
    {
#if NETSTANDARD
        if (File.Exists(destinationPath))
        {
            var attributes = File.GetAttributes(destinationPath);
            var replaceableAttributes =
                attributes & ~(FileAttributes.Hidden | FileAttributes.ReadOnly);
            if (replaceableAttributes != attributes)
            {
                File.SetAttributes(destinationPath, replaceableAttributes);
            }
        }
#endif
        File.Move(sourcePath, destinationPath, overwrite: true);
    }

    private async ValueTask WriteAtomicAsync(
        string destinationPath,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken
    )
    {
        var attempt = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var temporaryPath = destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
#if NETSTANDARD
                using (
#else
                await using (
#endif
                    var stream = new FileStream(
                        temporaryPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        // The complete payload is already buffered; avoid another large stream buffer.
                        bufferSize: 1,
                        FileOptions.Asynchronous | FileOptions.WriteThrough
                    )
                )
                {
                    await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                MoveReplacing(temporaryPath, destinationPath);
                return;
            }
            catch (IOException) when (attempt < _options.RetryCount)
            {
                attempt++;
                var retryDelayFactory = _options.RetryDelayFactory;
                var retryDelay = _options.RetryDelay;
                if (retryDelayFactory is not null)
                {
                    retryDelay = retryDelayFactory(attempt);
                }

                if (retryDelay < TimeSpan.Zero)
                {
                    throw new InvalidOperationException(
                        "The retry delay factory returned a negative delay."
                    );
                }

                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                    // A failed cleanup is harmless; the unique temporary name cannot shadow a later write.
                }
            }
        }
    }
}
