namespace Configlue;

/// <summary>Validates a resolved or edited configuration model.</summary>
/// <typeparam name="T">The configuration model type.</typeparam>
public interface IConfiglueValidator<in T>
{
    /// <summary>Returns validation failures, or an empty list when the value is valid.</summary>
    IReadOnlyList<string> Validate(T value);
}
