namespace Configlue;

/// <summary>Convenience helpers for optional interface capabilities.</summary>
/// <remarks>Advanced helpers for validation and diagnostics capabilities.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
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
}
