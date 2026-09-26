namespace Configlue;

/// <summary>An immutable priority-ordered set of logical state sources.</summary>
public sealed class StateSourceSet<T>
{
    private readonly StateSource<T>[] _sources;

    /// <summary>Creates a source set ordered by descending priority.</summary>
    public StateSourceSet(IEnumerable<StateSource<T>> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sources = sources.ToArray();
        if (_sources.Length == 0)
        {
            throw new ArgumentException("At least one state source is required.", nameof(sources));
        }

        if (_sources.Any(static source => source is null))
        {
            throw new ArgumentException("A source set cannot contain null sources.", nameof(sources));
        }

        var duplicate = _sources.GroupBy(static source => source.Id, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"Source id '{duplicate.Key}' is registered more than once.", nameof(sources));
        }

        Array.Sort(_sources, static (left, right) => right.Priority.CompareTo(left.Priority));
    }

    /// <summary>The sources in read-priority order.</summary>
    public IReadOnlyList<StateSource<T>> Sources => _sources;
}
