namespace Configlue;

/// <summary>Resolves the current application-defined subject for a scoped options operation.</summary>
public interface IConfiglueSubjectAccessor
{
    /// <summary>Asynchronously resolves the current subject as the common Configlue contract.</summary>
    ValueTask<IConfiglueSubject> GetCurrentSubjectAsync(
        CancellationToken cancellationToken = default
    );
}

/// <summary>Asynchronously resolves the current subject using its application-defined type.</summary>
/// <typeparam name="TSubject">The application-defined subject type.</typeparam>
public interface IConfiglueSubjectAccessor<TSubject> : IConfiglueSubjectAccessor
    where TSubject : IConfiglueSubject
{
    /// <summary>Resolves the current subject.</summary>
    ValueTask<TSubject> GetCurrentAsync(CancellationToken cancellationToken = default);

    /// <inheritdoc />
    async ValueTask<IConfiglueSubject> IConfiglueSubjectAccessor.GetCurrentSubjectAsync(
        CancellationToken cancellationToken
    ) => await GetCurrentAsync(cancellationToken).ConfigureAwait(false);
}
