using System.ComponentModel.DataAnnotations;
using Configlue;
using Configlue.Provider.Json;
using Configlue.Source.Environment;
using Configlue.Testing;

namespace Configlue.Tests;

[ConfiglueModel("read-validation-root", Version = 1)]
public partial class ReadValidationRoot
{
    public ReadValidationNested? Nested { get; set; } = new();
}

[ConfiglueModel("read-validation-nested", Version = 1)]
public partial class ReadValidationNested
{
    public string Name { get; set; } = "default";

    [Range(1, 65535)]
    public int Port { get; set; } = 5432;
}

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
    public async Task IgnoreValue_DropsOnlyInvalidNestedMember()
    {
        var options = new ConfiglueOptions<ReadValidationRoot, ReadValidationRoot.Fragment>(
            new StateSourceSet<ReadValidationRoot.Fragment>([
                new StateSource<ReadValidationRoot.Fragment>(
                    "nested-layer",
                    new InMemoryStateStore<ReadValidationRoot.Fragment>(
                        new ReadValidationRoot.Fragment
                        {
                            Nested = Optional<ReadValidationNested.Fragment?>.Present(
                                new ReadValidationNested.Fragment
                                {
                                    Name = Optional<string>.Present("kept"),
                                    Port = Optional<int>.Present(0),
                                }
                            ),
                        }
                    )
                ),
            ]),
            readValidationMode: ReadValidationMode.IgnoreValue
        );

        var resolved = await options.ReadAsync();

        (resolved.Value!.Nested!.Name).ShouldBe("kept");
        (resolved.Value.Nested.Port).ShouldBe(5432);
    }

    [Test]
    public async Task DisablingDataAnnotationsKeepsAnnotatedValuesInEachReadMode()
    {
        foreach (var mode in Enum.GetValues<ReadValidationMode>())
        {
            var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
                new StateSourceSet<AppSettings.Fragment>([
                    new StateSource<AppSettings.Fragment>(
                        "layer",
                        new InMemoryStateStore<AppSettings.Fragment>(
                            new AppSettings.Fragment { RetryCount = Optional<int>.Present(150) }
                        )
                    ),
                ]),
                validateDataAnnotations: false,
                readValidationMode: mode
            );

            var resolved = await options.ReadAsync();

            (resolved.Status).ShouldBe(StateReadStatus.Success);
            (resolved.Value!.RetryCount).ShouldBe(150);
        }
    }

    [Test]
    public async Task StrictThrowRunsCustomValidatorsWhenDataAnnotationsAreDisabled()
    {
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new StateSource<AppSettings.Fragment>(
                    "layer",
                    new InMemoryStateStore<AppSettings.Fragment>(
                        new AppSettings.Fragment
                        {
                            Label = Optional<string?>.Present("custom-invalid"),
                        }
                    )
                ),
            ]),
            validators: [new InvalidLabelValidator()],
            validateDataAnnotations: false,
            readValidationMode: ReadValidationMode.StrictThrow
        );

        var failure = await Should.ThrowAsync<ConfiglueValidationException>(async () =>
            await options.ReadAsync()
        );

        (string.Join("; ", failure.Failures)).ShouldContain("custom validator");
    }

    [Test]
    public async Task InvalidFallbackConditionContinuesToLowerPrioritySource()
    {
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new StateSource<AppSettings.Fragment>(
                    "invalid",
                    new StubReader(
                        StateReadResult<AppSettings.Fragment>.Invalid(new AppSettings.Fragment())
                    ),
                    priority: 100,
                    fallbackCondition: StateFallbackCondition.Invalid
                ),
                new StateSource<AppSettings.Fragment>(
                    "valid",
                    new InMemoryStateStore<AppSettings.Fragment>(
                        new AppSettings.Fragment { RetryCount = Optional<int>.Present(8) }
                    )
                ),
            ])
        );

        var resolved = await options.ReadAsync();

        (resolved.Status).ShouldBe(StateReadStatus.Success);
        (resolved.Value!.RetryCount).ShouldBe(8);
    }

    [Test]
    public async Task ReadValidationMode_IsConfigurableThroughTheModelBuilder()
    {
        await using var context = ConfiglueApp.CreateContext(builder =>
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
        var options = (IConfiglueRuntimeOptions<AppSettings>)context.GetOptions<AppSettings>();

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
        var readable = (IConfiglueRuntimeOptions<AppSettings>)options;
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
        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                    sources.FromJsonFile(new JsonFileSourceOptions { Id = "file", Path = path })
                )
            );
        });
        var options = (IConfiglueRuntimeOptions<AppSettings>)context.GetOptions<AppSettings>();
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
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, content);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(temporaryPath, path, overwrite: true);
                    return;
                }
                catch (IOException) when (attempt < 50)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100));
                }
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
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

    private sealed class InvalidLabelValidator : IConfiglueValidator<AppSettings>
    {
        public IReadOnlyList<string> Validate(AppSettings value) =>
            value.Label == "custom-invalid" ? ["custom validator rejected Label"] : [];
    }
}
