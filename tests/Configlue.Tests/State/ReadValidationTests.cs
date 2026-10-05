using System.ComponentModel.DataAnnotations;
using Configlue;
using Configlue.Provider.Json;
using Configlue.Source.Environment;
using Configlue.Sources;
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

[ConfiglueModel("review.compare", Version = 1)]
public partial class CompareSettings
{
    public string Expected { get; set; } = "same";

    [Compare(nameof(Expected))]
    public string Actual { get; set; } = "same";
}

[ConfiglueModel("review.compare-nested", Version = 1)]
public partial class CompareNestedSettings
{
    public string Expected { get; set; } = "same";

    [Compare(nameof(Expected))]
    public string Actual { get; set; } = "same";
}

[ConfiglueModel("review.compare-root", Version = 1)]
public partial class CompareRootSettings
{
    public CompareNestedSettings? Nested { get; set; } = new();
}

[ConfiglueModel("review.context-capture", Version = 1)]
public partial class ContextCaptureSettings
{
    public string Expected { get; set; } = "same";

    [CapturesValidationContext]
    public string Actual { get; set; } = "same";

    [Range(0, 100)]
    public int RetryCount { get; set; } = 3;
}

public sealed class CapturesValidationContextAttribute : ValidationAttribute
{
    public static object? LastContainer;
    public static string? LastMemberName;
    public static object? LastValue;

    public static void Reset()
    {
        LastContainer = null;
        LastMemberName = null;
        LastValue = null;
    }

    protected override ValidationResult? IsValid(
        object? value,
        ValidationContext validationContext
    )
    {
        LastContainer = validationContext.ObjectInstance;
        LastMemberName = validationContext.MemberName;
        LastValue = value;
        return ValidationResult.Success;
    }
}

public sealed class ReadValidationTests
{
    [Test]
    public async Task EffectiveModel_ThrowsForInvalidResolvedValue()
    {
        var variables = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["APP__RETRYCOUNT"] = "150",
        };
        var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
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
    public async Task EffectiveModel_ShadowedInvalidContributionIsIgnored()
    {
        // Only the final resolved model is validated: an invalid low-priority
        // contribution that is fully shadowed by a valid higher-priority value
        // must not fail the read.
        var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new StateSource<AppSettings.Fragment>("low", new InMemoryStateSource<AppSettings.Fragment>(
                        new AppSettings.Fragment { RetryCount = Optional<int>.Present(150) }
                    ), new StateSourceOptions<AppSettings.Fragment> { Priority = 100 }),
                new StateSource<AppSettings.Fragment>("high", new InMemoryStateSource<AppSettings.Fragment>(
                        new AppSettings.Fragment { RetryCount = Optional<int>.Present(5) }
                    ), new StateSourceOptions<AppSettings.Fragment> { Priority = 200 }),
            ])
        );

        var resolved = await options.ReadAsync();

        (resolved.Status).ShouldBe(StateReadStatus.Success);
        (resolved.Value!.RetryCount).ShouldBe(5);
    }

    [Test]
    public async Task EffectiveModel_NestedInvalidMemberThrows()
    {
        var options = new ConfiglueRuntime<ReadValidationRoot, ReadValidationRoot.Fragment>(
            new StateSourceSet<ReadValidationRoot.Fragment>([
                new StateSource<ReadValidationRoot.Fragment>("nested-layer", new InMemoryStateSource<ReadValidationRoot.Fragment>(
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
                    ), new StateSourceOptions<ReadValidationRoot.Fragment>()),
            ])
        );

        var failure = await Should.ThrowAsync<ConfiglueValidationException>(async () =>
            await options.ReadAsync()
        );

        (failure.Failures.Count > 0).ShouldBeTrue();
    }

    [Test]
    public async Task DisablingDataAnnotationsKeepsAnnotatedValues()
    {
        var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new StateSource<AppSettings.Fragment>("layer", new InMemoryStateSource<AppSettings.Fragment>(
                        new AppSettings.Fragment { RetryCount = Optional<int>.Present(150) }
                    ), new StateSourceOptions<AppSettings.Fragment>()),
            ]),
            validateDataAnnotations: false
        );

        var resolved = await options.ReadAsync();

        (resolved.Status).ShouldBe(StateReadStatus.Success);
        (resolved.Value!.RetryCount).ShouldBe(150);
    }

    [Test]
    public async Task CustomValidatorRunsWhenDataAnnotationsAreDisabled()
    {
        var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new StateSource<AppSettings.Fragment>("layer", new InMemoryStateSource<AppSettings.Fragment>(
                        new AppSettings.Fragment
                        {
                            Label = Optional<string?>.Present("custom-invalid"),
                        }
                    ), new StateSourceOptions<AppSettings.Fragment>()),
            ]),
            validators: [new InvalidLabelValidator()],
            validateDataAnnotations: false
        );

        var failure = await Should.ThrowAsync<ConfiglueValidationException>(async () =>
            await options.ReadAsync()
        );

        (string.Join("; ", failure.Failures)).ShouldContain("custom validator");
    }

    [Test]
    public async Task CustomValidatorRunsForEffectiveModel()
    {
        var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new StateSource<AppSettings.Fragment>("layer", new InMemoryStateSource<AppSettings.Fragment>(
                        new AppSettings.Fragment
                        {
                            Label = Optional<string?>.Present("custom-invalid"),
                        }
                    ), new StateSourceOptions<AppSettings.Fragment>()),
            ]),
            validators: [new InvalidLabelValidator()]
        );

        var failure = await Should.ThrowAsync<ConfiglueValidationException>(async () =>
            await options.ReadAsync()
        );

        (string.Join("; ", failure.Failures)).ShouldContain("custom validator");
    }

    [Test]
    public async Task InvalidPayloadFallbackConditionContinuesToLowerPrioritySource()
    {
        var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new StateSource<AppSettings.Fragment>("invalid", new StubReader(
                        StateReadResult<AppSettings.Fragment>.InvalidPayload(
                            new AppSettings.Fragment()
                        )
                    ), new StateSourceOptions<AppSettings.Fragment> { Priority = 100, FallbackCondition = StateFallbackCondition.InvalidPayload }),
                new StateSource<AppSettings.Fragment>("valid", new InMemoryStateSource<AppSettings.Fragment>(
                        new AppSettings.Fragment { RetryCount = Optional<int>.Present(8) }
                    ), new StateSourceOptions<AppSettings.Fragment>()),
            ])
        );

        var resolved = await options.ReadAsync();

        (resolved.Status).ShouldBe(StateReadStatus.Success);
        (resolved.Value!.RetryCount).ShouldBe(8);
    }

    [Test]
    public async Task EffectiveValidationFailure_DoesNotTriggerSourceFallback()
    {
        // The high-priority source returns a successful read with a value that fails DataAnnotations.
        // Even though it advertises every source-local fallback condition, a validation failure must
        // never be mistaken for a source-local read outcome that allows the lower-priority source
        // to take over.
        var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new StateSource<AppSettings.Fragment>("invalid-value", new InMemoryStateSource<AppSettings.Fragment>(
                        new AppSettings.Fragment { RetryCount = Optional<int>.Present(150) }
                    ), new StateSourceOptions<AppSettings.Fragment> { Priority = 100, FallbackCondition = StateFallbackCondition.NotFoundOrUnavailable
                        | StateFallbackCondition.InvalidPayload }),
                new StateSource<AppSettings.Fragment>("valid", new InMemoryStateSource<AppSettings.Fragment>(
                        new AppSettings.Fragment { RetryCount = Optional<int>.Present(8) }
                    ), new StateSourceOptions<AppSettings.Fragment>()),
            ])
        );

        var failure = await Should.ThrowAsync<ConfiglueValidationException>(async () =>
            await options.ReadAsync()
        );

        (failure.Failures.Count > 0).ShouldBeTrue();
    }

    [Test]
    public async Task InvalidPayloadStatus_FlowsThroughProvenanceWithoutThrowingOnRead()
    {
        var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new StateSource<AppSettings.Fragment>("invalid-layer", new StubReader(
                        StateReadResult<AppSettings.Fragment>.InvalidPayload(
                            new AppSettings.Fragment(),
                            "rev-1"
                        )
                    ), new StateSourceOptions<AppSettings.Fragment>()),
            ])
        );

        var result = await options.ReadAsync();

        (result.Status).ShouldBe(StateReadStatus.InvalidPayload);
        (result.SourceId).ShouldBe(SourceId.From("invalid-layer"));
        var readable = (IConfiglueRuntimeState<AppSettings>)options;
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
        var options = (IConfiglueRuntimeState<AppSettings>)context.GetState<AppSettings>();
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

    [Test]
    public async Task EffectiveModel_CompareValid_ReadsSuccessfully()
    {
        var options = new ConfiglueRuntime<CompareSettings, CompareSettings.Fragment>(
            new StateSourceSet<CompareSettings.Fragment>([
                new StateSource<CompareSettings.Fragment>("layer", new InMemoryStateSource<CompareSettings.Fragment>(
                        new CompareSettings.Fragment
                        {
                            Expected = Optional<string>.Present("same"),
                            Actual = Optional<string>.Present("same"),
                        }
                    ), new StateSourceOptions<CompareSettings.Fragment>()),
            ])
        );

        var resolved = await options.ReadAsync();

        (resolved.Status).ShouldBe(StateReadStatus.Success);
        (resolved.Value!.Expected).ShouldBe("same");
        (resolved.Value.Actual).ShouldBe("same");
    }

    [Test]
    public async Task EffectiveModel_CompareMismatch_ThrowsValidationWithoutArgumentNull()
    {
        var options = new ConfiglueRuntime<CompareSettings, CompareSettings.Fragment>(
            new StateSourceSet<CompareSettings.Fragment>([
                new StateSource<CompareSettings.Fragment>("layer", new InMemoryStateSource<CompareSettings.Fragment>(
                        new CompareSettings.Fragment
                        {
                            Expected = Optional<string>.Present("same"),
                            Actual = Optional<string>.Present("different"),
                        }
                    ), new StateSourceOptions<CompareSettings.Fragment>()),
            ])
        );

        Exception? thrown = null;
        try
        {
            await options.ReadAsync();
        }
        catch (Exception exception)
        {
            thrown = exception;
        }

        (thrown).ShouldNotBeNull();
        (thrown).ShouldBeOfType<ConfiglueValidationException>();
    }

    [Test]
    public async Task EffectiveModel_NestedCompareValid_ReadsSuccessfully()
    {
        var options = new ConfiglueRuntime<CompareRootSettings, CompareRootSettings.Fragment>(
            new StateSourceSet<CompareRootSettings.Fragment>([
                new StateSource<CompareRootSettings.Fragment>("layer", new InMemoryStateSource<CompareRootSettings.Fragment>(
                        new CompareRootSettings.Fragment
                        {
                            Nested = Optional<CompareNestedSettings.Fragment?>.Present(
                                new CompareNestedSettings.Fragment
                                {
                                    Expected = Optional<string>.Present("same"),
                                    Actual = Optional<string>.Present("same"),
                                }
                            ),
                        }
                    ), new StateSourceOptions<CompareRootSettings.Fragment>()),
            ])
        );

        var resolved = await options.ReadAsync();

        (resolved.Status).ShouldBe(StateReadStatus.Success);
        (resolved.Value!.Nested!.Actual).ShouldBe("same");
    }

    [Test]
    public async Task EffectiveModel_NestedCompareMismatch_ThrowsValidation()
    {
        var options = new ConfiglueRuntime<CompareRootSettings, CompareRootSettings.Fragment>(
            new StateSourceSet<CompareRootSettings.Fragment>([
                new StateSource<CompareRootSettings.Fragment>("layer", new InMemoryStateSource<CompareRootSettings.Fragment>(
                        new CompareRootSettings.Fragment
                        {
                            Nested = Optional<CompareNestedSettings.Fragment?>.Present(
                                new CompareNestedSettings.Fragment
                                {
                                    Expected = Optional<string>.Present("same"),
                                    Actual = Optional<string>.Present("different"),
                                }
                            ),
                        }
                    ), new StateSourceOptions<CompareRootSettings.Fragment>()),
            ])
        );

        var failure = await Should.ThrowAsync<ConfiglueValidationException>(async () =>
            await options.ReadAsync()
        );

        (failure.Failures.Count > 0).ShouldBeTrue();
    }

    [Test]
    public async Task MemberValidation_PassesContainerInstanceAndMemberNameToContext()
    {
        CapturesValidationContextAttribute.Reset();
        var options = new ConfiglueRuntime<
            ContextCaptureSettings,
            ContextCaptureSettings.Fragment
        >(
            new StateSourceSet<ContextCaptureSettings.Fragment>([
                new StateSource<ContextCaptureSettings.Fragment>("layer", new InMemoryStateSource<ContextCaptureSettings.Fragment>(
                        new ContextCaptureSettings.Fragment
                        {
                            Expected = Optional<string>.Present("same"),
                            Actual = Optional<string>.Present("same"),
                        }
                    ), new StateSourceOptions<ContextCaptureSettings.Fragment>()),
            ])
        );

        var resolved = await options.ReadAsync();

        (resolved.Status).ShouldBe(StateReadStatus.Success);
        (CapturesValidationContextAttribute.LastContainer).ShouldNotBeNull();
        (
            CapturesValidationContextAttribute.LastContainer
        ).ShouldBeOfType<ContextCaptureSettings>();
        ((ContextCaptureSettings)CapturesValidationContextAttribute.LastContainer!).Expected
            .ShouldBe("same");
        (CapturesValidationContextAttribute.LastMemberName).ShouldBe("Actual");
        (CapturesValidationContextAttribute.LastValue).ShouldBe("same");
    }

    [Test]
    public async Task DisablingDataAnnotations_SkipsCompareValidation()
    {
        var options = new ConfiglueRuntime<CompareSettings, CompareSettings.Fragment>(
            new StateSourceSet<CompareSettings.Fragment>([
                new StateSource<CompareSettings.Fragment>("layer", new InMemoryStateSource<CompareSettings.Fragment>(
                        new CompareSettings.Fragment
                        {
                            Expected = Optional<string>.Present("same"),
                            Actual = Optional<string>.Present("different"),
                        }
                    ), new StateSourceOptions<CompareSettings.Fragment>()),
            ]),
            validateDataAnnotations: false
        );

        var resolved = await options.ReadAsync();

        (resolved.Status).ShouldBe(StateReadStatus.Success);
        (resolved.Value!.Actual).ShouldBe("different");
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
                    if (File.Exists(path))
                        File.Replace(temporaryPath, path, destinationBackupFileName: null);
                    else
                        File.Move(temporaryPath, path);
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
        : ISourceReader<AppSettings.Fragment>
    {
        public ValueTask<StateReadResult<AppSettings.Fragment>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            _ = context;
            return ValueTaskCompat.FromResult(result);
        }
    }

    private sealed class InvalidLabelValidator : IConfiglueValidator<AppSettings>
    {
        public IReadOnlyList<string> Validate(AppSettings value) =>
            value.Label == "custom-invalid" ? ["custom validator rejected Label"] : [];
    }
}
