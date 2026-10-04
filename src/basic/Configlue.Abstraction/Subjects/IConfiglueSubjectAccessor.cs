namespace Configlue;

/// <summary>Resolves the current application-defined subject for a subject-scoped state operation.</summary>
/// <remarks>Subjects scope operations inside one state instance; they never select another instance.
/// Advanced application API for subject-scoped state.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public interface IConfiglueSubjectAccessor
{
    /// <summary>Asynchronously resolves the current subject as the common Configlue contract.</summary>
    ValueTask<IConfiglueSubject> GetCurrentSubjectAsync(
        CancellationToken cancellationToken = default
    );
}

/// <summary>Asynchronously resolves the current subject using its application-defined type.</summary>
/// <remarks>Advanced application API for subject-scoped state.</remarks>
/// <typeparam name="TSubject">The application-defined subject type.</typeparam>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public interface IConfiglueSubjectAccessor<TSubject> : IConfiglueSubjectAccessor
    where TSubject : IConfiglueSubject
{
    /// <summary>Resolves the current subject.</summary>
    ValueTask<TSubject> GetCurrentAsync(CancellationToken cancellationToken = default);
}
