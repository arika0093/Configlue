namespace Configlue;

/// <summary>Creates fixed-subject views over one shared configuration runtime.</summary>
/// <typeparam name="T">The configuration model type.</typeparam>
public interface ISubjectOptions<T>
{
    /// <summary>Gets the ordinary writable Options view fixed to <paramref name="subject"/>.</summary>
    IWritableOptions<T> For(IConfiglueSubject subject);
}
