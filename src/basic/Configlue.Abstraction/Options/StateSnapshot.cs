namespace Configlue;

/// <summary>Resolves one consistent state snapshot, including generated details, without a second read.</summary>
/// <typeparam name="T">The configuration model type.</typeparam>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public interface IConfiglueStateSnapshotRuntime<T>
{
    /// <summary>Resolves the current value and its details transport from one resolution.</summary>
    ValueTask<StateSnapshot<T>> GetSnapshotAsync(CancellationToken cancellationToken = default);
}

/// <summary>One resolved effective value together with the details transport that produced it.</summary>
/// <typeparam name="T">The configuration model type.</typeparam>
public sealed class StateSnapshot<T>
{
    /// <summary>Creates a resolved snapshot.</summary>
    /// <param name="value">The resolved effective value.</param>
    /// <param name="details">The details transport resolved alongside <paramref name="value"/>.</param>
    public StateSnapshot(T value, Configlue.CompilerServices.ConfiglueDetailsSnapshot? details)
    {
        Value = value;
        Details = details;
    }

    /// <summary>The resolved effective value.</summary>
    public T Value { get; }

    /// <summary>The details transport for this same resolution; null for snapshots without details.</summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public Configlue.CompilerServices.ConfiglueDetailsSnapshot? Details { get; }
}

/// <summary>Convenience helpers for reading resolved state snapshots.</summary>
public static class ConfiglueStateSnapshotExtensions
{
    /// <summary>Resolves one consistent snapshot for a state view.</summary>
    /// <remarks>Throws <see cref="NotSupportedException"/> when the state view cannot expose snapshots.</remarks>
    public static ValueTask<StateSnapshot<T>> GetSnapshotAsync<T>(
        this IReadOnlyState<T> state,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state is IConfiglueStateSnapshotRuntime<T> runtime)
        {
            return runtime.GetSnapshotAsync(cancellationToken);
        }

        throw new NotSupportedException(
            "This state implementation does not expose resolved snapshots."
        );
    }
}
