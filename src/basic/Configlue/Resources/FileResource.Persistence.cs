using System.Runtime.InteropServices;
using Configlue.Codecs;
using Configlue.State;

namespace Configlue.Resources;

public sealed partial class FileResource
{
    /// <inheritdoc />
    public async ValueTask<ResourceReadResult> ReadAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        _ = ConfiglueResourceContext.Normalize(context);
        try
        {
            var content = await ReadFileSnapshotAsync(_path, cancellationToken)
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
        catch (UnauthorizedAccessException)
        {
            // Same transient-lock reasoning as the watcher revision probe.
            return ResourceReadResult.Unavailable();
        }
    }

    /// <inheritdoc />
    public ValueTask<StateWriteResult> WriteAsync(
        ConfiglueResourceContext context,
        ResourceWriteRequest request,
        CancellationToken cancellationToken = default
    )
    {
        _ = ConfiglueResourceContext.Normalize(context);
        return WriteBatchAsync(
            [ResourceWriteMutation.Replace(request, context)],
            cancellationToken
        );
    }

    /// <inheritdoc />
    public async ValueTask<StateWriteResult> WriteBatchAsync(
        IReadOnlyList<ResourceWriteMutation> mutations,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        ResourceWriteMutation.ValidateBatch(mutations);
        Directory.CreateDirectory(_directory);

        using var processLock = await AcquireProcessLockAsync(_path, cancellationToken)
            .ConfigureAwait(false);
        var checkRevision = mutations.Any(static mutation => !mutation.Condition.IsNone);
        var canSkipRead =
            !checkRevision
            && !_options.CreateBackup
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
        if (_options.CreateBackup && previousContent is not null)
        {
            await CreateBackupAsync(previousContent, cancellationToken).ConfigureAwait(false);
        }

        await WriteAtomicAsync(_path, content, cancellationToken).ConfigureAwait(false);
        return new StateWriteResult(GetRevision(content.Span));
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

    /// <summary>Restores the single backup without creating another backup.</summary>
    /// <remarks>Advanced recovery primitive; backups are never restored automatically.</remarks>
    /// <exception cref="FileNotFoundException">No backup exists.</exception>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
    public async ValueTask<StateWriteResult> RestoreLatestBackupAsync(
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_directory);
        using var processLock = await AcquireProcessLockAsync(_path, cancellationToken)
            .ConfigureAwait(false);
        var backupPath = GetLatestBackupPath();
        if (backupPath is null)
        {
            throw new FileNotFoundException(
                "No backup exists for this file resource.",
                _backupPath
            );
        }

        var content = await File.ReadAllBytesAsync(backupPath, cancellationToken)
            .ConfigureAwait(false);
        await WriteAtomicAsync(_path, content, cancellationToken).ConfigureAwait(false);
        return new StateWriteResult(GetRevision(content));
    }

    private async ValueTask<byte[]?> TryReadForWriteAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await ReadFileSnapshotAsync(_path, cancellationToken).ConfigureAwait(false);
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

    private static async ValueTask<byte[]> ReadFileSnapshotAsync(
        string path,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        // An open reader owns the old file snapshot while an atomic replacement publishes
        // a new file. Delete sharing allows that replacement on Windows as well as Unix.
#if NETSTANDARD
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
#else
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
#endif
            FileShare.Read | FileShare.Delete,
            bufferSize: 1,
            FileOptions.Asynchronous | FileOptions.SequentialScan
        );
        if (stream.Length > int.MaxValue)
        {
            throw new IOException("The file is too large to read into memory.");
        }
        var content = new byte[(int)stream.Length];
        var offset = 0;
        while (offset < content.Length)
        {
            var count = await stream
                .ReadAsync(content.AsMemory(offset), cancellationToken)
                .ConfigureAwait(false);
            if (count == 0)
            {
                Array.Resize(ref content, offset);
                break;
            }
            offset += count;
        }
        return content;
    }

    private static void MoveReplacing(string sourcePath, string destinationPath)
    {
        if (File.Exists(destinationPath))
        {
#if NETSTANDARD
            var attributes = File.GetAttributes(destinationPath);
            var replaceableAttributes =
                attributes & ~(FileAttributes.Hidden | FileAttributes.ReadOnly);
            if (replaceableAttributes != attributes)
            {
                File.SetAttributes(destinationPath, replaceableAttributes);
            }
#endif
            // ReplaceFile supports open readers with delete sharing on Windows. It also
            // avoids the copy/delete overwrite polyfill used by .NET Standard consumers.
            File.Replace(sourcePath, destinationPath, destinationBackupFileName: null);
        }
        else
        {
            File.Move(sourcePath, destinationPath);
        }
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
                break;
            }
            catch (IOException) when (attempt < _options.RetryCount)
            {
                attempt++;
                await Task.Delay(_options.RetryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException) when (attempt < _options.RetryCount)
            {
                // Windows AV/indexer or a concurrent reader can lock either the
                // temporary or the destination briefly; retry like a sharing violation.
                attempt++;
                await Task.Delay(_options.RetryDelay, cancellationToken).ConfigureAwait(false);
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
                catch (UnauthorizedAccessException)
                {
                    // Same as above; the uniquely named temp file cannot collide.
                }
            }
        }
    }
}
