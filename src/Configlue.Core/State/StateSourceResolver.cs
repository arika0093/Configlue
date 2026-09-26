namespace Configlue;

/// <summary>Reads the first successful state from a priority-ordered set of sources.</summary>
public sealed class StateSourceResolver<T> : IStateReader<T>
{
    private readonly StateSourceSet<T> _sourceSet;
    private StateSource<T>? _activeSource;

    /// <summary>Creates a source resolver.</summary>
    public StateSourceResolver(StateSourceSet<T> sourceSet)
    {
        ArgumentNullException.ThrowIfNull(sourceSet);
        _sourceSet = sourceSet;
    }

    /// <summary>The source that most recently supplied a value.</summary>
    public StateSource<T>? ActiveSource => Volatile.Read(ref _activeSource);

    /// <inheritdoc />
    public async ValueTask<StateReadResult<T>> ReadAsync(CancellationToken cancellationToken = default)
    {
        StateReadResult<T> lastResult = default;
        foreach (var source in _sourceSet.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await source.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            result = result.FromSource(source.Id, source.PhysicalOrigin);
            if (result.Status == StateReadStatus.Success)
            {
                Volatile.Write(ref _activeSource, source);
                return result;
            }

            if (!CanFallBack(source.FallbackCondition, result.Status))
            {
                return result;
            }

            lastResult = result;
        }

        return lastResult;
    }

    internal IReadOnlyList<StateSource<T>> GetSourcesForWatch()
    {
        var active = ActiveSource;
        return active is null
            ? _sourceSet.Sources
            : _sourceSet.Sources.Where(source => source.Priority >= active.Priority).ToArray();
    }

    private static bool CanFallBack(StateFallbackCondition condition, StateReadStatus status) =>
        status switch
        {
            StateReadStatus.NotFound => (condition & StateFallbackCondition.NotFound) != 0,
            StateReadStatus.Unavailable => (condition & StateFallbackCondition.Unavailable) != 0,
            _ => false,
        };
}
