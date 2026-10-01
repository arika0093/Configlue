namespace Configlue;

/// <summary>Creates fixed-subject views over one shared configuration runtime.</summary>
/// <typeparam name="T">The configuration model type.</typeparam>
public interface ISubjectState<T>
{
    /// <summary>Gets the ordinary writable state view fixed to <paramref name="subject"/>.</summary>
    IWritableState<T> ForSubject(IConfiglueSubject subject);

    /// <summary>Gets an edit-session view whose sessions stay fixed to <paramref name="subject"/>.</summary>
    IConfiglueEditSessions<T> EditSessionsForSubject(IConfiglueSubject subject);
}
