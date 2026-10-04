using Configlue.Sources;
using Configlue.State;

namespace Configlue.Tests;

public sealed class RuntimeResolutionScratchTests
{
    [Test]
    public async Task MultiSourceResolutionPreservesSuccessPositionAndRevisionOrder()
    {
        foreach (var sourceCount in new[] { 1, 2, 4, 16 })
        {
            foreach (var successIndex in new[] { 0, sourceCount / 2, sourceCount - 1 }.Distinct())
            {
                var sources = Enumerable
                    .Range(0, sourceCount)
                    .Select(index => new StateSource<AppSettings.Fragment>(
                        $"source-{index}",
                        new FixedReader(
                            index == successIndex
                                ? StateReadResult<AppSettings.Fragment>.Success(
                                    new AppSettings.Fragment
                                    {
                                        RetryCount = Optional<int>.Present(index + 1),
                                    },
                                    $"revision-{index}"
                                )
                                : StateReadResult<AppSettings.Fragment>.NotFound(
                                    $"revision-{index}"
                                )
                        ),
                        new StateSourceOptions<AppSettings.Fragment>
                        {
                            Priority = sourceCount - index,
                            FallbackCondition = StateFallbackCondition.NotFound,
                        }
                    ))
                    .ToArray();
                await using var runtime = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
                    new StateSourceSet<AppSettings.Fragment>(sources)
                );

                var result = await runtime.ReadAsync();

                result.Status.ShouldBe(StateReadStatus.Success);
                result.Value!.RetryCount.ShouldBe(successIndex + 1);
                result.Revisions!.Revisions.Count.ShouldBe(sourceCount);
                for (var index = 0; index < sourceCount; index++)
                {
                    result
                        .Revisions.Revisions[SourceId.From($"source-{index}")]
                        .ShouldBe($"revision-{index}");
                }
            }
        }
    }

    [Test]
    public async Task NestedRevisionsAreRetainedAfterScratchIsReleased()
    {
        var child = new StateRevisionVector([
            new StateRevision(SourceId.From("leaf"), "leaf-revision"),
        ]);
        var sources = Enumerable
            .Range(0, 4)
            .Select(index => new StateSource<AppSettings.Fragment>(
                $"source-{index}",
                new FixedReader(
                    StateReadResult<AppSettings.Fragment>.Success(
                        new AppSettings.Fragment { RetryCount = Optional<int>.Present(index) },
                        $"revision-{index}"
                    ) with
                    {
                        Revisions = index == 2 ? child : null,
                    }
                ),
                new StateSourceOptions<AppSettings.Fragment>
                {
                    Priority = 4 - index,
                    FallbackCondition = StateFallbackCondition.NotFound,
                }
            ))
            .ToArray();
        await using var runtime = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>(sources)
        );

        var result = await runtime.ReadAsync();

        result
            .Revisions!.TryGetNestedRevisions(SourceId.From("source-2"), out var retained)
            .ShouldBeTrue();
        retained.ShouldBeSameAs(child);
        retained!.Revisions[SourceId.From("leaf")].ShouldBe("leaf-revision");
    }

    [Test]
    public async Task ScratchIsReleasedAfterReadExceptionAndCancellation()
    {
        var throwingReader = new ThrowOnceReader();
        var sources = new StateSourceSet<AppSettings.Fragment>([
            new(
                "throw-once",
                throwingReader,
                new StateSourceOptions<AppSettings.Fragment> { Priority = 2 }
            ),
            new(
                "fallback",
                new FixedReader(
                    StateReadResult<AppSettings.Fragment>.Success(
                        new AppSettings.Fragment { RetryCount = Optional<int>.Present(8) },
                        "fallback-revision"
                    )
                ),
                new StateSourceOptions<AppSettings.Fragment> { Priority = 1 }
            ),
        ]);
        await using var runtime = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(sources);

        await Should.ThrowAsync<InvalidOperationException>(async () => await runtime.ReadAsync());

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await runtime.ReadAsync(cancellation.Token)
        );

        var result = await runtime.ReadAsync();
        result.Value!.RetryCount.ShouldBe(8);
        result.Revisions!.Revisions.Count.ShouldBe(2);
    }

    private sealed class FixedReader(StateReadResult<AppSettings.Fragment> result)
        : ISourceReader<AppSettings.Fragment>
    {
        public ValueTask<StateReadResult<AppSettings.Fragment>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            _ = context;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTaskCompat.FromResult(result);
        }
    }

    private sealed class ThrowOnceReader : ISourceReader<AppSettings.Fragment>
    {
        private int _calls;

        public ValueTask<StateReadResult<AppSettings.Fragment>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            _ = context;
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _calls) == 1)
            {
                throw new InvalidOperationException("read failed");
            }

            return ValueTaskCompat.FromResult(
                StateReadResult<AppSettings.Fragment>.NotFound("missing")
            );
        }
    }
}
