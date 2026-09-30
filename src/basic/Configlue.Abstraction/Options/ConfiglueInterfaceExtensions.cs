namespace Configlue;

/// <summary>Compatibility helpers for optional interface capabilities.</summary>
public static class ConfiglueInterfaceExtensions
{
    /// <summary>Returns validation failures for the named state.</summary>
    public static IReadOnlyList<string> Validate<T>(
        this IConfiglueValidator<T> validator,
        string? name,
        T value
    )
    {
        if (validator is null)
        {
            throw new ArgumentNullException(nameof(validator));
        }

        return validator is INamedConfiglueValidator<T> namedValidator
            ? namedValidator.Validate(name, value)
            : validator.Validate(value);
    }

    /// <summary>Subscribes to reload failures when the diagnostics implementation supports them.</summary>
    public static IDisposable OnReloadFailed<T>(
        this IConfiglueDiagnostics<T> diagnostics,
        Action<Exception> listener
    )
    {
        if (diagnostics is null)
        {
            throw new ArgumentNullException(nameof(diagnostics));
        }

        if (listener is null)
        {
            throw new ArgumentNullException(nameof(listener));
        }
        if (diagnostics is IConfiglueReloadFailureDiagnostics<T> reloadDiagnostics)
        {
            return reloadDiagnostics.OnReloadFailed(listener);
        }

        throw new NotSupportedException(
            "This state implementation does not support reload-failure notifications."
        );
    }

    /// <summary>Removes a state asynchronously, falling back to synchronous removal if needed.</summary>
    public static ValueTask<bool> TryRemoveAsync<T>(
        this IConfiglueStateRegistry<T> registry,
        string stateName
    )
    {
        if (registry is null)
        {
            throw new ArgumentNullException(nameof(registry));
        }
        return registry is IAsyncConfiglueStateRegistry<T> asyncRegistry
            ? asyncRegistry.TryRemoveAsync(stateName)
            : new ValueTask<bool>(registry.TryRemove(stateName));
    }

    /// <summary>Clears a registry asynchronously, falling back to synchronous clearing if needed.</summary>
    public static ValueTask ClearAsync<T>(this IConfiglueStateRegistry<T> registry)
    {
        if (registry is null)
        {
            throw new ArgumentNullException(nameof(registry));
        }
        if (registry is IAsyncConfiglueStateRegistry<T> asyncRegistry)
        {
            return asyncRegistry.ClearAsync();
        }

        registry.Clear();
        return default;
    }
}
