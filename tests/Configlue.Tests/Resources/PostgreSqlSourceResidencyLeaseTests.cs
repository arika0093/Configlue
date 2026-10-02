using Configlue.Provider.Json;
using Configlue.Source.PostgreSql;

namespace Configlue.Tests;

public sealed class PostgreSqlSourceResidencyLeaseTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DisposeDuringActiveOperationDefersBackendDisposalUntilCompletion(bool watch)
    {
        var operationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new BlockingPostgreSqlStateBackend(operationEntered, release);
        using var source = new PostgreSqlSource<string>(
            _ => new object(),
            _ => backend,
            "settings",
            new JsonStateValueSerializer<string>()
        );

        var read = Task.Run(async () =>
        {
            if (watch)
                await source.WaitForChangeAsync(
                    CreateContext("tenant-a", RouteKey.From("region-a")),
                    null
                );
            else
                await source.ReadAsync(CreateContext("tenant-a", RouteKey.From("region-a")));
        });
        await operationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        source.Dispose();
        backend.DisposeCount.ShouldBe(0);
        source.CachedBackendCount.ShouldBe(0);

        release.TrySetResult();
        await read;
        backend.DisposeCount.ShouldBe(1);

        source.Dispose();
        backend.DisposeCount.ShouldBe(1);
    }

    [Test]
    public async Task DisposeDuringActiveWriteDefersBackendDisposalUntilCompletion()
    {
        var operationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new BlockingPostgreSqlStateBackend(operationEntered, release);
        using var source = new PostgreSqlSource<string>(
            _ => new object(),
            _ => backend,
            "settings",
            new JsonStateValueSerializer<string>()
        );

        var write = Task.Run(async () =>
            await source.WriteAsync(
                CreateContext("tenant-a", RouteKey.From("region-a")),
                new StateWriteRequest<string>("1")
            )
        );
        await operationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        source.Dispose();
        backend.DisposeCount.ShouldBe(0);

        release.TrySetResult();
        await write;
        backend.DisposeCount.ShouldBe(1);
    }

    private static ConfiglueResourceContext CreateContext(
        string subject,
        RouteKey route = default,
        string? modelId = null
    )
    {
        var key = SubjectKey.From(subject);
        return new ConfiglueResourceContext(modelId, new FakeSubject(key), key, route);
    }

    private sealed record FakeSubject(SubjectKey Key) : IConfiglueSubject;

    private sealed class BlockingPostgreSqlStateBackend : IPostgreSqlStateBackend
    {
        private readonly TaskCompletionSource _entered;
        private readonly TaskCompletionSource _release;
        private int _disposeCount;

        public BlockingPostgreSqlStateBackend(
            TaskCompletionSource entered,
            TaskCompletionSource release
        )
        {
            _entered = entered;
            _release = release;
        }

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public async ValueTask<ResourceReadResult> ReadAsync(
            string resourceNamespace,
            string modelId,
            string subjectKey,
            CancellationToken cancellationToken
        )
        {
            _entered.TrySetResult();
            await _release.Task;
            return ResourceReadResult.NotFound();
        }

        public async ValueTask<StateWriteResult> WriteAsync(
            string resourceNamespace,
            string modelId,
            string subjectKey,
            ResourceWriteRequest request,
            CancellationToken cancellationToken
        )
        {
            _entered.TrySetResult();
            await _release.Task;
            return new StateWriteResult("1");
        }

        public async ValueTask WaitForChangeAsync(
            string resourceNamespace,
            string modelId,
            string subjectKey,
            string? observedRevision,
            CancellationToken cancellationToken
        )
        {
            _entered.TrySetResult();
            await _release.Task;
        }

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }
}
