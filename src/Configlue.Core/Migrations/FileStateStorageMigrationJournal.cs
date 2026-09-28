using System.Text.Json;
using System.Text.Json.Serialization;

namespace Configlue.Migrations;

/// <summary>Stores migration progress as one atomically replaced JSON file per migration ID.</summary>
/// <remarks>
/// Progress files use XXH3 hashes of migration IDs as names, so IDs do not become path components. Writes use
/// <see cref="FileResource"/> revision checks; concurrent writers based on stale progress fail instead of
/// silently replacing a newer journal entry.
/// </remarks>
public sealed class FileStateStorageMigrationJournal
    : IStateStorageMigrationJournal,
        IStateStorageMigrationLeaseProvider
{
    private readonly string _directoryPath;
    private readonly FileResourceOptions? _resourceOptions;

    /// <summary>Creates a journal under the supplied directory.</summary>
    public FileStateStorageMigrationJournal(
        string directoryPath,
        FileResourceOptions? resourceOptions = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        _directoryPath = Path.GetFullPath(directoryPath);
        _resourceOptions =
            resourceOptions ?? new FileResourceOptions { CreateBackup = false, BackupMaxCount = 0 };
    }

    /// <summary>The normalized directory containing migration progress files.</summary>
    public string DirectoryPath => _directoryPath;

    /// <inheritdoc />
    public async ValueTask<IDisposable> AcquireMigrationLeaseAsync(
        string migrationId,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationId);
        cancellationToken.ThrowIfCancellationRequested();
        var hash = ConfiglueHashing.GetXxHash3Hex(migrationId);
        using var resource = new FileResource(
            Path.Combine(_directoryPath, hash + ".lease"),
            _resourceOptions
        );
        return await resource.AcquireExclusiveLockAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<StateStorageMigrationProgress?> ReadAsync(
        string migrationId,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationId);
        cancellationToken.ThrowIfCancellationRequested();
        using var resource = CreateResource(migrationId);
        var read = await resource.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (read.Status == StateReadStatus.NotFound)
        {
            return null;
        }

        if (read.Status != StateReadStatus.Success)
        {
            throw new IOException(
                $"Migration journal '{migrationId}' could not be read: {read.Status}."
            );
        }

        var document =
            JsonSerializer.Deserialize(
                read.Content.Span,
                StateStorageMigrationJournalJsonContext
                    .Default
                    .StateStorageMigrationProgressDocument
            )
            ?? throw new InvalidDataException(
                $"Migration journal '{migrationId}' contains an empty progress document."
            );
        if (!string.Equals(document.MigrationId, migrationId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Migration journal '{migrationId}' contains progress for '{document.MigrationId}'."
            );
        }

        return new StateStorageMigrationProgress(
            document.MigrationId,
            document.SourceIds,
            document.TargetSourceIds,
            document.CompletedTargetSourceIds,
            document.RetireSources,
            document.SourcesRetired
        );
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(
        StateStorageMigrationProgress progress,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(progress);
        cancellationToken.ThrowIfCancellationRequested();
        using var resource = CreateResource(progress.MigrationId);
        var current = await resource.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (current.Status is not (StateReadStatus.Success or StateReadStatus.NotFound))
        {
            throw new IOException(
                $"Migration journal '{progress.MigrationId}' could not be read before writing: {current.Status}."
            );
        }

        var document = new StateStorageMigrationProgressDocument
        {
            MigrationId = progress.MigrationId,
            SourceIds = progress.SourceIds.ToArray(),
            TargetSourceIds = progress.TargetSourceIds.ToArray(),
            CompletedTargetSourceIds = progress.CompletedTargetSourceIds.ToArray(),
            RetireSources = progress.RetireSources,
            SourcesRetired = progress.SourcesRetired,
        };
        var content = JsonSerializer.SerializeToUtf8Bytes(
            document,
            StateStorageMigrationJournalJsonContext.Default.StateStorageMigrationProgressDocument
        );
        await resource
            .WriteAsync(
                new ResourceWriteRequest(content, current.Revision, CheckRevision: true),
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private FileResource CreateResource(string migrationId)
    {
        var hash = ConfiglueHashing.GetXxHash3Hex(migrationId);
        var path = Path.Combine(_directoryPath, hash + ".json");
        return new FileResource(path, _resourceOptions);
    }
}

internal sealed class StateStorageMigrationProgressDocument
{
    public string MigrationId { get; init; } = string.Empty;
    public string[] SourceIds { get; init; } = [];
    public string[] TargetSourceIds { get; init; } = [];
    public string[] CompletedTargetSourceIds { get; init; } = [];
    public bool RetireSources { get; init; }
    public bool SourcesRetired { get; init; }
}

[JsonSerializable(typeof(StateStorageMigrationProgressDocument))]
internal partial class StateStorageMigrationJournalJsonContext : JsonSerializerContext { }
