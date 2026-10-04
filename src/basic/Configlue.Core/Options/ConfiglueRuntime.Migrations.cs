using Configlue.CompilerServices;

namespace Configlue;

internal sealed partial class ConfiglueRuntime<TModel, TFragment>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    /// <inheritdoc />
    public ValueTask<StateSourceMigrationResult> MigrateSourceAsync(
        SourceId sourceId,
        SourceId targetId,
        CancellationToken cancellationToken = default
    ) => _migrations.MigrateSourceAsync(sourceId, targetId, cancellationToken);

    /// <inheritdoc />
    public ValueTask<StateSourceMigrationResult> MigrateSourceAsync(
        SourceKey<TModel> sourceKey,
        SourceKey<TModel> targetKey,
        CancellationToken cancellationToken = default
    ) => _migrations.MigrateSourceAsync(sourceKey, targetKey, cancellationToken);

    /// <inheritdoc />
    public ValueTask<StateStorageMigrationResult> MigrateSourcesToTargetsAsync(
        IEnumerable<SourceId> sourceIds,
        IReadOnlyDictionary<
            SourceId,
            Func<IConfiglueFragment, IConfiglueFragment>
        > targetProjections,
        CancellationToken cancellationToken = default,
        bool retireSources = false
    ) =>
        _migrations.MigrateSourcesToTargetsAsync(
            sourceIds,
            targetProjections,
            cancellationToken,
            retireSources
        );

    /// <inheritdoc />
    public ValueTask<StateStorageMigrationResult> MigrateSourcesToTargetsAsync(
        IEnumerable<SourceId> sourceIds,
        IReadOnlyDictionary<SourceId, Func<TFragment, TFragment>> targetProjections,
        CancellationToken cancellationToken = default,
        bool retireSources = false
    ) =>
        _migrations.MigrateSourcesToTargetsAsync(
            sourceIds,
            targetProjections,
            cancellationToken,
            retireSources
        );

    /// <inheritdoc />
    public ValueTask<StateStorageMigrationResult> MigrateSourcesToTargetsAsync(
        IEnumerable<SourceKey<TModel>> sourceKeys,
        IReadOnlyDictionary<
            SourceKey<TModel>,
            Func<IConfiglueFragment, IConfiglueFragment>
        > targetProjections,
        CancellationToken cancellationToken = default,
        bool retireSources = false
    ) =>
        _migrations.MigrateSourcesToTargetsAsync(
            sourceKeys,
            targetProjections,
            cancellationToken,
            retireSources
        );
}
