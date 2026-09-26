namespace Configlue;

/// <summary>Reads the current configuration value assembled from its state sources.</summary>
/// <typeparam name="T">The configuration model type.</typeparam>
public interface IReadOnlyOptions<T>
{
    /// <summary>Reads and resolves the current configuration value.</summary>
    ValueTask<StateReadResult<T>> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>Subscribes to resolved values when watched state sources change.</summary>
    IDisposable OnChange(Action<T> listener);

    /// <summary>Gets the current value, throwing when no usable state can be read.</summary>
    async ValueTask<T> GetValueAsync(CancellationToken cancellationToken = default)
    {
        var result = await ReadAsync(cancellationToken).ConfigureAwait(false);
        if (result.Status != StateReadStatus.Success)
        {
            throw new InvalidOperationException(
                $"Configuration state could not be read: {result.Status}."
            );
        }

        return result.Value!;
    }

    /// <summary>Explains a model property using its resolved value and present source contributions.</summary>
    ValueTask<ConfiglueValueExplanation> ExplainAsync(
        string propertyPath,
        CancellationToken cancellationToken = default
    );
}
