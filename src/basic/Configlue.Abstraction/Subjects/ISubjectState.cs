namespace Configlue;

/// <summary>Scopes operations on one state instance to an application subject.</summary>
/// <typeparam name="T">The configuration model type.</typeparam>
/// <remarks>
/// A subject never selects another state instance. The state instance stays <c>(TModel, StateName)</c>;
/// the subject only scopes the operation (who/what it is for) and affects source-specific
/// addressing (<see cref="ResourceKey"/>, <see cref="RouteKey"/>) and the resulting physical
/// <see cref="ResourceId"/>. Different subjects share one state definition but can resolve
/// different provider keys and routes.
/// </remarks>
/// <remarks>Advanced application API for subject-scoped state.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public interface ISubjectState<T>
{
    /// <summary>Gets a writable view that scopes operations on this state instance to <paramref name="subject"/>.</summary>
    IWritableState<T> ForSubject(IConfiglueSubject subject);

    /// <summary>Gets an edit-session view whose sessions stay scoped to <paramref name="subject"/>.</summary>
    IConfiglueEditSessions<T> EditSessionsForSubject(IConfiglueSubject subject);
}
