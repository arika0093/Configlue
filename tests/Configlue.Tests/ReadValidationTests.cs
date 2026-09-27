using Configlue;
using Configlue.Provider.Json;
using Configlue.Source.Environment;
using Configlue.Testing;

namespace Configlue.Tests;

public sealed class ReadValidationTests
{
    [Test]
    public async Task EffectiveThrow_ThrowsForInvalidResolvedValue()
    {
        var variables = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["APP__RETRYCOUNT"] = "150",
        };
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                EnvironmentStateSource.FromEnvironment<AppSettings, AppSettings.Fragment>(
                    "environment",
                    "APP",
                    environmentVariables: () => variables
                ),
            ])
        );

        var failure = await Should.ThrowAsync<ConfiglueValidationException>(async () =>
            await options.ReadAsync()
        );
        (failure.Failures.Count > 0).ShouldBeTrue();
    }

    [Test]
    public async Task StrictThrow_ThrowsAtOffendingSourceWhileEffectiveUsesResolvedValue()
    {
        StateSourceSet<AppSettings.Fragment> sources() =>
            new([
                new StateSource<AppSettings.Fragment>(
                    "low",
                    new InMemoryStateStore<AppSettings.Fragment>(
                        new AppSettings.Fragment { RetryCount = Optional<int>.Present(150) }
                    ),
                    priority: 100
                ),
                new StateSource<AppSettings.Fragment>(
                    "high",
                    new InMemoryStateStore<AppSettings.Fragment>(
                        new AppSettings.Fragment { RetryCount = Optional<int>.Present(5) }
                    ),
                    priority: 200
                ),
            ]);

        var strict = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
            sources(),
            readValidationMode: ReadValidationMode.StrictThrow
        );
        var strictFailure = await Should.ThrowAsync<ConfiglueValidationException>(async () =>
            await strict.ReadAsync()
        );
        (
            string.Join("; ", strictFailure.Failures).Contains("low", StringComparison.Ordinal)
        ).ShouldBeTrue();

        var effective = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
            sources(),
            readValidationMode: ReadValidationMode.EffectiveThrow
        );
        var resolved = await effective.ReadAsync();

        (resolved.Status).ShouldBe(StateReadStatus.Success);
        (resolved.Value!.RetryCount).ShouldBe(5);
    }

    [Test]
    public async Task IgnoreValue_DropsInvalidMembersAndKeepsTheRest()
    {
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new StateSource<AppSettings.Fragment>(
                    "layer",
                    new InMemoryStateStore<AppSettings.Fragment>(
                        new AppSettings.Fragment
                        {
                            RetryCount = Optional<int>.Present(150),
                            Label = Optional<string?>.Present("kept"),
                        }
                    )
                ),
            ]),
            readValidationMode: ReadValidationMode.IgnoreValue
        );

        var resolved = await options.ReadAsync();

        (resolved.Status).ShouldBe(StateReadStatus.Success);
        (resolved.Value!.RetryCount).ShouldBe(3);
        (resolved.Value.Label).ShouldBe("kept");
    }

    [Test]
    public async Task ReadValidationMode_IsConfigurableThroughTheModelBuilder()
    {
        await using var context = Configlue.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.ReadValidationMode = ReadValidationMode.IgnoreValue;
                model.Sources(sources =>
                    sources.Add(
                        new StateSource<AppSettings.Fragment>(
                            "layer",
                            new InMemoryStateStore<AppSettings.Fragment>(
                                new AppSettings.Fragment { RetryCount = Optional<int>.Present(150) }
                            )
                        )
                    )
                );
            });
        });
        var options = (IConfiglueOptions<AppSettings>)context.GetOptions<AppSettings>();

        ((await options.GetValueAsync()).RetryCount).ShouldBe(3);
    }

    [Test]
    public async Task InvalidStatus_FlowsThroughProvenanceWithoutThrowingOnRead()
    {
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new StateSource<AppSettings.Fragment>(
                    "invalid-layer",
                    new StubReader(
                        StateReadResult<AppSettings.Fragment>.Invalid(
                            new AppSettings.Fragment(),
                            "rev-1"
                        )
                    )
                ),
            ])
        );

        var result = await options.ReadAsync();

        (result.Status).ShouldBe(StateReadStatus.Invalid);
        (result.SourceId).ShouldBe("invalid-layer");
        var readable = (IConfiglueOptions<AppSettings>)options;
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await readable.GetValueAsync()
        );
    }

    [Test]
    public async Task Watch_NotifiesReloadFailedInsteadOfListenerForInvalidReloads()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "Configlue.Tests",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        await File.WriteAllTextAsync(path, "{\"$version\":2,\"RetryCount\":5}");
        await using var context = Configlue.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                    sources.FromJsonFile(new JsonFileSourceOptions { Id = "file", Path = path })
                )
            );
        });
        var options = (IConfiglueOptions<AppSettings>)context.GetOptions<AppSettings>();
        var listenerCalls = 0;
        var failure = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var _ = options.OnChange(_ => Interlocked.Increment(ref listenerCalls));
        using var __ = options.OnReloadFailed(exception => failure.TrySetResult(exception));
        ((await options.GetValueAsync()).RetryCount).ShouldBe(5);

        await WriteAllTextWithRetryAsync(path, "{\"$version\":2,\"RetryCount\":150}");

        var reported = await failure.Task.WaitAsync(TimeSpan.FromSeconds(10));
        (reported).ShouldBeOfType<ConfiglueValidationException>();
        (Volatile.Read(ref listenerCalls)).ShouldBe(0);
        Directory.Delete(directory, recursive: true);
    }

    private static async Task WriteAllTextWithRetryAsync(string path, string content)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await File.WriteAllTextAsync(path, content);
                return;
            }
            catch (IOException) when (attempt < 50)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }
        }
    }

    private sealed class StubReader(StateReadResult<AppSettings.Fragment> result)
        : IStateReader<AppSettings.Fragment>
    {
        public ValueTask<StateReadResult<AppSettings.Fragment>> ReadAsync(
            CancellationToken cancellationToken = default
        ) => ValueTask.FromResult(result);
    }
}
