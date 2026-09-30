namespace Configlue;

/// <summary>Validates a resolved or edited configuration model.</summary>
/// <typeparam name="T">The configuration model type.</typeparam>
public interface IConfiglueValidator<in T>
{
    /// <summary>Returns validation failures, or an empty list when the value is valid.</summary>
    IReadOnlyList<string> Validate(T value);
}

/// <summary>Validates a model value in the context of a named state.</summary>
public interface INamedConfiglueValidator<in T> : IConfiglueValidator<T>
{
    /// <summary>Returns validation failures for the named state.</summary>
    IReadOnlyList<string> Validate(string? name, T value);
}
