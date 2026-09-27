using System.Diagnostics;

namespace Configlue;

public sealed partial class ConfiglueProfiledOptions<TModel, TFragment>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_initialized)
        {
            return;
        }

        var previousActiveProfileName = _catalog?.ActiveProfileName;

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var result = await _catalogSource
                .Reader.ReadAsync(cancellationToken)
                .ConfigureAwait(false);
            ConfiglueProfileCatalog catalog;
            bool needsWrite;
            if (result.Status == StateReadStatus.Success)
            {
                if (result.Value is null)
                {
                    throw new InvalidDataException(
                        "The profile catalog source returned a null catalog."
                    );
                }

                catalog = Normalize(result.Value, out needsWrite);
                _catalogRevision = result.Revision;
            }
            else if (result.Status == StateReadStatus.NotFound)
            {
                catalog = CreateDefaultCatalog();
                needsWrite = true;
                _catalogRevision = result.Revision;
            }
            else
            {
                throw new InvalidOperationException(
                    $"The profile catalog could not be read: {result.Status}."
                );
            }

            if (needsWrite)
            {
                try
                {
                    var writeResult = await _catalogSource
                        .Writer!.WriteAsync(
                            new StateWriteRequest<ConfiglueProfileCatalog>(
                                catalog,
                                result.Revision,
                                CheckRevision: true
                            ),
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                    _catalogRevision = writeResult.Revision;
                }
                catch (StateConflictException) when (attempt < 4)
                {
                    continue;
                }
            }

            await SynchronizeRegistryAsync(catalog).ConfigureAwait(false);
            _catalog = catalog;
            _initialized = true;
            StartCatalogWatcher();
            if (
                previousActiveProfileName is not null
                && !string.Equals(
                    previousActiveProfileName,
                    catalog.ActiveProfileName,
                    StringComparison.Ordinal
                )
            )
            {
                EnqueueActiveProfileNotification(catalog.ActiveProfileName!);
            }
            return;
        }

        throw new StateConflictException(
            "The profile catalog changed repeatedly during initialization."
        );
    }

    private async Task PersistCatalogAsync(
        ConfiglueProfileCatalog catalog,
        CancellationToken cancellationToken
    )
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var expectedCatalog = _catalog!;
            var current = await _catalogSource
                .Reader.ReadAsync(cancellationToken)
                .ConfigureAwait(false);
            if (current.Status != StateReadStatus.Success || current.Value is null)
            {
                await RefreshCatalogAfterConflictAsync(cancellationToken).ConfigureAwait(false);
                throw new StateConflictException(
                    "The profile catalog changed before it could be updated."
                );
            }

            var currentCatalog = Normalize(current.Value, out _);
            if (!CatalogEquals(currentCatalog, expectedCatalog))
            {
                await RefreshCatalogAfterConflictAsync(cancellationToken).ConfigureAwait(false);
                throw new StateConflictException(
                    "The profile catalog was updated by another process."
                );
            }

            // Profile values can share the catalog's physical resource and advance its resource revision.
            // Refresh the token while the logical catalog still matches before attempting a conditional write.
            try
            {
                var writeResult = await _catalogSource
                    .Writer!.WriteAsync(
                        new StateWriteRequest<ConfiglueProfileCatalog>(
                            catalog,
                            current.Revision,
                            CheckRevision: true
                        ),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
                _catalogRevision = writeResult.Revision;
                _catalog = catalog;
                return;
            }
            catch (StateConflictException)
            {
                await RefreshCatalogAfterConflictAsync(cancellationToken).ConfigureAwait(false);
                if (attempt < 2 && CatalogEquals(_catalog!, expectedCatalog))
                {
                    continue;
                }

                throw;
            }
            catch
            {
                // A writer may report failure after the catalog reached durable storage.
                // Re-read on the next operation before trusting the cached catalog.
                _initialized = false;
                throw;
            }
        }

        throw new StateConflictException(
            "The profile catalog changed repeatedly while it was being updated."
        );
    }

    private async Task RefreshCatalogAfterConflictAsync(CancellationToken cancellationToken)
    {
        _initialized = false;
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
    }
}
