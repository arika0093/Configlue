namespace Configlue;

/// <summary>
/// Owns the ambient per-subject scope for one runtime.
///
/// <see cref="AsyncLocal{T}"/> is itself thread-safe, so no lock is needed.
/// Resource-context derivation (subject key/route mapping) lives here so read,
/// write, watch, and migration code resolves subjects through one owner.
/// </summary>
internal sealed class RuntimeSubjectContext
{
    private readonly AsyncLocal<IConfiglueSubject?> _current = new();

    /// <summary>The subject ambient to the current async flow, if any.</summary>
    internal IConfiglueSubject? Current => _current.Value;

    /// <summary>The ambient subject key, or the default key outside a subject scope.</summary>
    internal SubjectKey CurrentKey => _current.Value?.Key ?? default;

    /// <summary>Enters a subject scope, restoring the previous subject on dispose.</summary>
    internal IDisposable Enter(IConfiglueSubject subject)
    {
        var previous = _current.Value;
        _current.Value = subject;
        return new SubjectScope(_current, previous);
    }

    internal ConfiglueResourceContext GetResourceContext<TFragment>(
        StateSource<TFragment> source,
        ConfiglueResourceContext @default
    )
        where TFragment : class, IConfiglueFragment<TFragment> =>
        _current.Value is { } subject ? source.GetResourceContext(subject) : @default;

    internal ResourceId? GetResourceId<TFragment>(
        StateSource<TFragment> source,
        ConfiglueResourceContext @default
    )
        where TFragment : class, IConfiglueFragment<TFragment> =>
        source.GetResourceId(GetResourceContext(source, @default));

    private sealed class SubjectScope(
        AsyncLocal<IConfiglueSubject?> context,
        IConfiglueSubject? previous
    ) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                context.Value = previous;
            }
        }
    }
}
