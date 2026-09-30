using Configlue.CompilerServices;

namespace Configlue;

/// <summary>
/// Owns a scoped state runtime and the resources created for it, disposing both with the scope.
/// </summary>
/// <remarks>
/// A scoped runtime consumes scoped host services such as a circuit-scoped browser JavaScript
/// runtime. The holder keeps the runtime and its owned resources alive for exactly one
/// dependency-injection scope, preventing a singleton from capturing scoped dependencies.
/// </remarks>
internal sealed class ConfiglueScopedRuntime<TModel> : IDisposable, IAsyncDisposable
    where TModel : IConfiglueFacadeModel<TModel>
{
    private readonly IDisposable[] _resources;
    private int _disposed;

    internal ConfiglueScopedRuntime(
        IConfiglueRuntimeState<TModel> runtime,
        IReadOnlyList<IDisposable> resources
    )
    {
        Runtime = runtime;
        _resources = [.. resources];
    }

    /// <summary>The scoped runtime instance.</summary>
    internal IConfiglueRuntimeState<TModel> Runtime { get; }

    /// <inheritdoc />
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        List<Exception>? errors = null;
        try
        {
            if (Runtime is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else if (Runtime is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        catch (Exception exception)
        {
            (errors ??= []).Add(exception);
        }

        foreach (var resource in _resources)
        {
            try
            {
                resource.Dispose();
            }
            catch (Exception exception)
            {
                (errors ??= []).Add(exception);
            }
        }

        if (errors is not null)
        {
            throw new AggregateException(
                "One or more scoped Configlue resources failed to dispose.",
                errors
            );
        }
    }
}
