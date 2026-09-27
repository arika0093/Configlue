namespace Configlue;

/// <summary>Exposes source administration and diagnostics beyond the regular options API.</summary>
/// <typeparam name="T">The configuration model type.</typeparam>
public interface IConfiglueOptions<T> : IWritableOptions<T>
{
    /// <summary>Synchronously resolves the current value from the registered sources.</summary>
    /// <remarks>This blocks when a source read is asynchronous. Use <see cref="IReadOnlyOptions{T}.GetValueAsync"/> from asynchronous flows.</remarks>
    T CurrentValue => GetValueAsync(CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>Subscribes to failures while the background watcher reads changed state.</summary>
    /// <remarks>Receives thrown watcher/reload exceptions and an <see cref="InvalidOperationException"/> when a changed state resolves to a non-success status. Failures from explicit read calls and change listeners are not reported here.</remarks>
    IDisposable OnReloadFailed(Action<Exception> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        throw new NotSupportedException(
            "This options implementation does not support reload-failure notifications."
        );
    }

    /// <summary>Returns the configured source topology and registration-level write routing.</summary>
    ConfiglueOptionsDiagnostics GetDiagnostics();

    /// <summary>Explains a model property using its resolved value and present source contributions.</summary>
    ValueTask<ConfiglueValueExplanation> ExplainAsync(
        string propertyPath,
        CancellationToken cancellationToken = default
    );

    /// <summary>Migrates one source's contribution into another writable source without merging unrelated sources.</summary>
    ValueTask<StateSourceMigrationResult> MigrateSourceAsync(
        string sourceId,
        string targetId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Migrates selected source contributions into projected targets. Implementations verify each write and
    /// skip targets already holding the requested fragment, so a partially completed operation can be retried.
    /// When <paramref name="retireSources"/> is true, selected sources are removed from this options instance
    /// after all targets verify and only if the effective model remains unchanged.
    /// </summary>
    ValueTask<StateStorageMigrationResult> MigrateSourcesToTargetsAsync(
        IEnumerable<string> sourceIds,
        IReadOnlyDictionary<string, Func<IConfiglueFragment, IConfiglueFragment>> targetProjections,
        CancellationToken cancellationToken = default,
        bool retireSources = false
    );

    /// <summary>Applies explicit source-local patches and groups compatible writes by physical resource identity.</summary>
    ValueTask<StateMultiWriteResult> ApplyPatchesAsync(
        IEnumerable<StateSourcePatch> patches,
        CancellationToken cancellationToken = default
    );
}
