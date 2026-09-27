namespace Configlue;

/// <summary>Creates a source for the fragment type fixed by a generated model descriptor.</summary>
/// <remarks>
/// Provider packages implement this interface to expose non-generic source helpers on
/// <see cref="ConfiglueSourceSetBuilder"/>. The facade invokes the generic method from generated
/// code, so helper registrations do not need reflection or a public Fragment type argument.
/// Resources created by the definition must be reported through its <c>ownResource</c> callback;
/// resources supplied by the application must not be reported.
/// </remarks>
public interface IConfiglueSourceDefinition
{
    /// <summary>Creates the typed source when a context materializes its registration.</summary>
    StateSource<TFragment> Create<TFragment>(
        ConfiglueModelSchema modelSchema,
        IServiceProvider? serviceProvider,
        Action<IDisposable> ownResource
    )
        where TFragment : class, IConfiglueFragment<TFragment>;
}
