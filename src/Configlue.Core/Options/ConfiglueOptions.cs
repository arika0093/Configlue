namespace Configlue;

/// <summary>Resolves and saves a generated configuration model over a set of state sources.</summary>
/// <typeparam name="TModel">The generated configuration model.</typeparam>
/// <typeparam name="TFragment">The model's generated sparse fragment.</typeparam>
public sealed class ConfiglueOptions<TModel, TFragment> : IWritableOptions<TModel>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private readonly StateSourceSet<TFragment> _sourceSet;
    private readonly StateWriteRoute _writeRoute;

    /// <summary>Creates options backed by the supplied state sources.</summary>
    public ConfiglueOptions(StateSourceSet<TFragment> sourceSet, StateWriteRoute writeRoute = default)
    {
        ArgumentNullException.ThrowIfNull(sourceSet);
        _sourceSet = sourceSet;
        _writeRoute = writeRoute;
    }

    /// <inheritdoc />
    public async ValueTask<StateReadResult<TModel>> ReadAsync(CancellationToken cancellationToken = default)
    {
        var contributions = new List<(StateSource<TFragment> Source, StateReadResult<TFragment> Result)>();
        var revisions = new List<StateRevision>();
        StateReadResult<TFragment> lastFailure = default;

        foreach (var source in _sourceSet.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = (await source.Reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                .FromSource(source.Id, source.PhysicalOrigin);
            revisions.Add(new StateRevision(source.Id, result.Revision));
            if (result.Status == StateReadStatus.Success)
            {
                if (result.Value is null)
                {
                    throw new InvalidOperationException($"State source '{source.Id}' returned a null configuration fragment.");
                }

                contributions.Add((source, result));
                continue;
            }

            lastFailure = result;
            if (!CanFallBack(source.FallbackCondition, result.Status))
            {
                return new StateReadResult<TModel>(
                    result.Status,
                    default,
                    result.Revision,
                    result.SourceId,
                    result.PhysicalOrigin,
                    result.Schema,
                    new StateRevisionVector(revisions));
            }
        }

        if (contributions.Count == 0 && lastFailure.Status == StateReadStatus.Unavailable)
        {
            return new StateReadResult<TModel>(
                lastFailure.Status,
                default,
                lastFailure.Revision,
                lastFailure.SourceId,
                lastFailure.PhysicalOrigin,
                lastFailure.Schema,
                new StateRevisionVector(revisions));
        }

        var merged = TFragment.Empty;
        for (var index = contributions.Count - 1; index >= 0; index--)
        {
            merged = merged.Merge(contributions[index].Result.Value!);
        }

        var model = TModel.FromFragment(merged);
        var active = contributions.FirstOrDefault();
        return StateReadResult<TModel>.Success(
            model,
            active.Result.Revision,
            TModel.ConfiglueSchema.ToMetadata()) with
        {
            SourceId = active.Source?.Id,
            PhysicalOrigin = active.Source?.PhysicalOrigin,
            Revisions = new StateRevisionVector(revisions),
        };
    }

    /// <inheritdoc />
    public async ValueTask<StateWriteResult> SaveAsync(TModel value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = SelectWriteSource();
        var current = await source.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (current.Status == StateReadStatus.Unavailable)
        {
            throw new InvalidOperationException($"Cannot safely write configuration because source '{source.Id}' is unavailable.");
        }

        var fragment = TModel.ToFragment(value);
        return await source.Writer!.WriteAsync(
            new StateWriteRequest<TFragment>(fragment, current.Revision, CheckRevision: true),
            cancellationToken).ConfigureAwait(false);
    }

    private StateSource<TFragment> SelectWriteSource()
    {
        var source = _writeRoute.SourceId is { } id
            ? _sourceSet.Sources.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal))
            : _sourceSet.Sources.FirstOrDefault(static candidate => candidate.Writer is not null);

        if (source is null)
        {
            throw new InvalidOperationException(_writeRoute.SourceId is { } sourceId
                ? $"State source '{sourceId}' is not registered."
                : "No writable state source is registered.");
        }

        if (source.Writer is null)
        {
            throw new InvalidOperationException($"State source '{source.Id}' does not support writes.");
        }

        return source;
    }

    private static bool CanFallBack(StateFallbackCondition condition, StateReadStatus status) =>
        status switch
        {
            StateReadStatus.NotFound => (condition & StateFallbackCondition.NotFound) != 0,
            StateReadStatus.Unavailable => (condition & StateFallbackCondition.Unavailable) != 0,
            _ => false,
        };
}
