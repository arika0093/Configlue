using Configlue.Resources;
using Configlue.Sources;
using Configlue.State;

namespace Configlue.Tests;

internal static class TestSourceExtensions
{
    public static ValueTask<StateReadResult<T>> ReadAsync<T>(
        this ISourceReader<T> reader,
        CancellationToken cancellationToken = default
    ) => reader.ReadAsync(ConfiglueResourceContext.Default, cancellationToken);

    public static ValueTask<StateWriteResult> WriteAsync<T>(
        this ISourceWriter<T> writer,
        StateWriteRequest<T> request,
        CancellationToken cancellationToken = default
    ) => writer.WriteAsync(ConfiglueResourceContext.Default, request, cancellationToken);

    public static ValueTask WaitForChangeAsync(
        this ISourceWatcher watcher,
        string? observedRevision,
        CancellationToken cancellationToken = default
    ) =>
        watcher.WaitForChangeAsync(
            ConfiglueResourceContext.Default,
            observedRevision,
            cancellationToken
        );

    public static ValueTask<StateReadResult<T>> ReadAsync<T>(
        this StateSource<T> source,
        CancellationToken cancellationToken = default
    ) => source.ReadAsync(ConfiglueResourceContext.Default, cancellationToken);

    public static ValueTask<StateWriteResult> WriteAsync<T>(
        this StateSource<T> source,
        StateWriteRequest<T> request,
        CancellationToken cancellationToken = default
    ) => source.WriteAsync(ConfiglueResourceContext.Default, request, cancellationToken);

    public static ValueTask WaitForChangeAsync<T>(
        this StateSource<T> source,
        string? observedRevision,
        CancellationToken cancellationToken = default
    ) =>
        source.WaitForChangeAsync(
            ConfiglueResourceContext.Default,
            observedRevision,
            cancellationToken
        );

    public static ValueTask<ResourceReadResult> ReadAsync(
        this IResourceReader reader,
        CancellationToken cancellationToken = default
    ) => reader.ReadAsync(ConfiglueResourceContext.Default, cancellationToken);

    public static ValueTask<StateWriteResult> WriteAsync(
        this IResourceWriter writer,
        ResourceWriteRequest request,
        CancellationToken cancellationToken = default
    ) => writer.WriteAsync(ConfiglueResourceContext.Default, request, cancellationToken);

    public static ValueTask<PipelineResourceReadResult> ReadPipelineAsync(
        this IPipelineResourceReader reader,
        CancellationToken cancellationToken = default
    ) => reader.ReadPipelineAsync(ConfiglueResourceContext.Default, cancellationToken);
}
