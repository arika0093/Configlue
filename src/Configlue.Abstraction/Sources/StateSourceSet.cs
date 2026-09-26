namespace Configlue;

/// <summary>An immutable priority-ordered set of logical state sources.</summary>
public sealed class StateSourceSet<T>
{
    private readonly StateSource<T>[] _sources;
    private readonly IReadOnlyList<StateSource<T>> _sourceView;

    /// <summary>Creates a source set ordered by descending priority.</summary>
    public StateSourceSet(IEnumerable<StateSource<T>> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var sourceItems = sources.ToArray();
        if (sourceItems.Length == 0)
        {
            throw new ArgumentException("At least one state source is required.", nameof(sources));
        }

        if (sourceItems.Any(static source => source is null))
        {
            throw new ArgumentException("A source set cannot contain null sources.", nameof(sources));
        }

        var duplicate = sourceItems.GroupBy(static source => source.Id, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"Source id '{duplicate.Key}' is registered more than once.", nameof(sources));
        }

        _sources = sourceItems
            .Select(static (source, index) => (source, index))
            .OrderByDescending(static item => item.source.Priority)
            .ThenBy(static item => item.index)
            .Select(static item => item.source)
            .ToArray();
        _sourceView = Array.AsReadOnly(_sources);
    }

    /// <summary>The sources in read-priority order.</summary>
    public IReadOnlyList<StateSource<T>> Sources => _sourceView;
}
