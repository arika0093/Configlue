using System.Net;
using System.Text;
using System.Text.Json;
using Amazon.AppConfigData;
using Amazon.AppConfigData.Model;
using Amazon.Runtime;
using Amazon.Runtime.Endpoints;
using Configlue.Provider.Json;
using Configlue.Resource.AwsAppConfig;
using Configlue.Testing;

namespace Configlue.Tests;

public sealed class AwsAppConfigResourceTests
{
    private const string SettingsV1 = """{"$version":2,"RetryCount":7,"Label":"from-appconfig"}""";
    private const string SettingsV2 = """{"$version":2,"RetryCount":9,"Label":"updated"}""";

    [Test]
    public async Task ReadAsync_StartsSessionWithIdentifiersAndReturnsPayload()
    {
        var client = new FakeAppConfigDataClient { NextStartToken = "initial-token" };
        client.EnqueueData("next-token", SettingsV1, TimeSpan.FromSeconds(15), "v1");
        using var resource = new AwsAppConfigResource(
            client,
            "my-app",
            "production",
            "settings",
            delayAsync: (_, _) => Task.CompletedTask
        );

        var result = await resource.ReadAsync();

        result.Status.ShouldBe(StateReadStatus.Success);
        result.Revision.ShouldBe("v1");
        Encoding.UTF8.GetString(result.Content.ToArray()).ShouldBe(SettingsV1);
        client.StartCount.ShouldBe(1);
        client.LastApplicationId.ShouldBe("my-app");
        client.LastEnvironmentId.ShouldBe("production");
        client.LastConfigurationProfileId.ShouldBe("settings");
        client.SeenTokens.ShouldBe(["initial-token"]);
        resource.CurrentTokenForTests.ShouldBe("next-token");
        var health = resource.GetHealthSnapshot();
        health.HasValue.ShouldBeTrue();
        health.LastRevision.ShouldBe("v1");
        health.LastPollInterval.ShouldBe(TimeSpan.FromSeconds(15));
        health.LastError.ShouldBeNull();
    }

    [Test]
    public async Task ReadAsync_EmptyInitialResponse_MapsToNotFound()
    {
        var client = new FakeAppConfigDataClient();
        client.EnqueueData("next-token", json: null, TimeSpan.FromSeconds(15));
        using var resource = CreateDirectResource(client);

        var result = await resource.ReadAsync();

        result.Status.ShouldBe(StateReadStatus.NotFound);
        resource.GetHealthSnapshot().HasValue.ShouldBeFalse();
    }

    [Test]
    public async Task ReadAsync_EmptyResponseAfterValue_ReturnsCachedPayloadWithSameRevision()
    {
        var client = new FakeAppConfigDataClient();
        client.EnqueueData("t1", SettingsV1, TimeSpan.FromMilliseconds(10), "v1");
        client.EnqueueData("t2", json: null, TimeSpan.FromMilliseconds(10));
        using var resource = CreateDirectResource(client);

        var first = await resource.ReadAsync();
        var second = await resource.ReadAsync();

        first.Revision.ShouldBe("v1");
        second.Status.ShouldBe(StateReadStatus.Success);
        second.Revision.ShouldBe("v1");
        Encoding.UTF8.GetString(second.Content.ToArray()).ShouldBe(SettingsV1);
        client.SeenTokens.ShouldBe(["initial", "t1"]);
    }

    [Test]
    public async Task ReadAsync_ChangedPayload_UpdatesRevisionAndContent()
    {
        var client = new FakeAppConfigDataClient();
        client.EnqueueData("t1", SettingsV1, TimeSpan.FromMilliseconds(10), "v1");
        client.EnqueueData("t2", SettingsV2, TimeSpan.FromMilliseconds(10), "v2");
        using var resource = CreateDirectResource(client);

        await resource.ReadAsync();
        var second = await resource.ReadAsync();

        second.Revision.ShouldBe("v2");
        Encoding.UTF8.GetString(second.Content.ToArray()).ShouldBe(SettingsV2);
    }

    [Test]
    public async Task ReadAsync_WithoutVersionLabel_FallsBackToContentHashRevision()
    {
        var client = new FakeAppConfigDataClient();
        client.EnqueueData("t1", SettingsV1, TimeSpan.FromMilliseconds(10));
        client.EnqueueData("t2", SettingsV1, TimeSpan.FromMilliseconds(10));
        using var resource = CreateDirectResource(client);

        var first = await resource.ReadAsync();
        var second = await resource.ReadAsync();

        first.Revision.ShouldNotBeNullOrWhiteSpace();
        second.Revision.ShouldBe(first.Revision);
    }

    [Test]
    public async Task ReadAsync_ForwardsRequiredMinimumPollIntervalToSessionStart()
    {
        var client = new FakeAppConfigDataClient();
        client.EnqueueData("t1", SettingsV1, TimeSpan.FromSeconds(30), "v1");
        using var resource = new AwsAppConfigResource(
            client,
            "app",
            "env",
            "profile",
            new AwsAppConfigResourceOptions { RequiredMinimumPollIntervalInSeconds = 45 },
            delayAsync: (_, _) => Task.CompletedTask
        );

        await resource.ReadAsync();

        client.LastRequiredMinimumPollIntervalInSeconds.ShouldBe(45);
    }

    [Test]
    public async Task Watcher_ReturnsImmediatelyWhenRevisionAlreadyChanged()
    {
        var client = new FakeAppConfigDataClient();
        client.EnqueueData("t1", SettingsV1, TimeSpan.FromMilliseconds(10), "v1");
        using var resource = CreateDirectResource(client);
        await resource.ReadAsync();

        await resource.WaitForChangeAsync("stale-revision");
    }

    [Test]
    public async Task Watcher_WaitsForServerIntervalsAndSignalsOnlyOnChange()
    {
        var client = new FakeAppConfigDataClient();
        client.EnqueueData("t1", SettingsV1, TimeSpan.FromMilliseconds(11), "v1");
        client.EnqueueData("t2", json: null, TimeSpan.FromMilliseconds(22));
        client.EnqueueData("t3", json: null, TimeSpan.FromMilliseconds(33));
        client.EnqueueData("t4", SettingsV2, TimeSpan.FromMilliseconds(44), "v2");
        client.EnqueueData("t5", SettingsV2, TimeSpan.FromMilliseconds(44), "v2");
        var delays = new RecordingDelay();
        using var resource = new AwsAppConfigResource(
            client,
            "app",
            "env",
            "profile",
            delayAsync: delays.InvokeAsync
        );
        var initial = await resource.ReadAsync();
        initial.Revision.ShouldBe("v1");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await resource.WaitForChangeAsync("v1", timeout.Token);

        delays.Recorded.ShouldBe(
            [
                TimeSpan.FromMilliseconds(11),
                TimeSpan.FromMilliseconds(22),
                TimeSpan.FromMilliseconds(33),
            ]
        );
        client.SeenTokens.ShouldBe(["initial", "t1", "t2", "t3"]);
        var reread = await resource.ReadAsync(timeout.Token);
        reread.Revision.ShouldBe("v2");
        resource.CurrentTokenForTests.ShouldBe("t5");
    }

    [Test]
    public async Task Watcher_StaysPendingThroughNoChangeResponses()
    {
        // Real pacing delays: an immediate fake delay would let the refresh loop
        // busy-spin and starve the test's own continuations.
        var client = new FakeAppConfigDataClient { RepeatEmptyWhenExhausted = true };
        client.EnqueueData("t1", SettingsV1, TimeSpan.FromMilliseconds(10), "v1");
        using var resource = new AwsAppConfigResource(
            client,
            "app",
            "env",
            "profile"
        );
        await resource.ReadAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var wait = resource.WaitForChangeAsync("v1", timeout.Token).AsTask();
        await client.WaitForPollCountAsync(3).WaitAsync(TimeSpan.FromSeconds(10));

        wait.IsCompleted.ShouldBeFalse();
        resource.GetHealthSnapshot().ChangeCount.ShouldBe(1);
        timeout.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => await wait);
    }

    [Test]
    public async Task ReadAsync_RestartsSessionAfterExpiredToken()
    {
        var client = new FakeAppConfigDataClient { NextStartToken = "restarted" };
        client.EnqueueFailure(new AwsAppConfigSessionExpiredException("expired"));
        client.EnqueueData("after-restart", SettingsV1, TimeSpan.FromSeconds(15), "v1");
        using var resource = CreateDirectResource(client);

        var result = await resource.ReadAsync();

        result.Status.ShouldBe(StateReadStatus.Success);
        result.Revision.ShouldBe("v1");
        client.StartCount.ShouldBe(2);
        resource.GetHealthSnapshot().SessionRestarts.ShouldBe(1);
    }

    [Test]
    public async Task Watcher_RestartsSessionAfterExpiredTokenWithoutSignaling()
    {
        var client = new FakeAppConfigDataClient();
        client.NextStartToken = "second-session";
        client.EnqueueData("t1", SettingsV1, TimeSpan.FromMilliseconds(10), "v1");
        client.EnqueueFailure(new AwsAppConfigSessionExpiredException("expired"));
        client.EnqueueData("t2", SettingsV2, TimeSpan.FromMilliseconds(10), "v2");
        using var resource = CreateDirectResource(client);
        await resource.ReadAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await resource.WaitForChangeAsync("v1", timeout.Token);

        client.StartCount.ShouldBe(2);
        resource.GetHealthSnapshot().SessionRestarts.ShouldBe(1);
        resource.GetHealthSnapshot().LastRevision.ShouldBe("v2");
    }

    [Test]
    public async Task ReadAsync_RetainsLastGoodStateThroughTransientFailures()
    {
        var client = new FakeAppConfigDataClient();
        client.EnqueueData("t1", SettingsV1, TimeSpan.FromMilliseconds(10), "v1");
        client.EnqueueFailure(new AwsAppConfigTransientException("throttled"));
        using var resource = CreateDirectResource(client);
        await resource.ReadAsync();

        var result = await resource.ReadAsync();

        result.Status.ShouldBe(StateReadStatus.Success);
        result.Revision.ShouldBe("v1");
        Encoding.UTF8.GetString(result.Content.ToArray()).ShouldBe(SettingsV1);
        var health = resource.GetHealthSnapshot();
        health.LastError.ShouldNotBeNull();
        health.LastError!.ShouldContain("throttled");
        health.HasValue.ShouldBeTrue();
        health.LastRevision.ShouldBe("v1");
    }

    [Test]
    public async Task ReadAsync_WithoutCachedValue_TransientFailureMapsToUnavailable()
    {
        var client = new FakeAppConfigDataClient();
        client.EnqueueFailure(new AwsAppConfigTransientException("service down"));
        using var resource = CreateDirectResource(client);

        var result = await resource.ReadAsync();

        result.Status.ShouldBe(StateReadStatus.Unavailable);
        result.Content.IsEmpty.ShouldBeTrue();
        resource.GetHealthSnapshot().HasValue.ShouldBeFalse();
        resource.GetHealthSnapshot().LastError.ShouldNotBeNull();
        resource.GetHealthSnapshot().LastError!.ShouldContain("service down");
    }

    [Test]
    public async Task ReadAsync_PermanentFailure_ThrowsWithoutReplacingState()
    {
        var client = new FakeAppConfigDataClient();
        client.EnqueueData("t1", SettingsV1, TimeSpan.FromMilliseconds(10), "v1");
        client.EnqueueFailure(new AwsAppConfigException("access denied"));
        using var resource = CreateDirectResource(client);
        await resource.ReadAsync();

        await Should.ThrowAsync<AwsAppConfigException>(async () => await resource.ReadAsync());

        var health = resource.GetHealthSnapshot();
        health.HasValue.ShouldBeTrue();
        health.LastRevision.ShouldBe("v1");
        health.LastError.ShouldNotBeNull();
        health.LastError!.ShouldContain("access denied");
    }

    [Test]
    public async Task WaitForChangeAsync_HonorsCancellation()
    {
        var client = new FakeAppConfigDataClient();
        client.PollAsyncBehavior = (_, token) =>
        {
            var completion = new TaskCompletionSource<AppConfigPollResult>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            token.Register(static state =>
                ((TaskCompletionSource<AppConfigPollResult>)state!).TrySetCanceled(),
                completion
            );
            return completion.Task;
        };
        using var resource = new AwsAppConfigResource(
            client,
            "app",
            "env",
            "profile",
            delayAsync: (_, _) => Task.CompletedTask
        );
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var wait = resource.WaitForChangeAsync(null, timeout.Token).AsTask();

        await Task.Delay(50, timeout.Token);
        timeout.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(async () => await wait);
    }

    [Test]
    public void Dispose_WakesPendingWatcherWithObjectDisposed()
    {
        var client = new FakeAppConfigDataClient();
        client.PollAsyncBehavior = (_, token) =>
        {
            var completion = new TaskCompletionSource<AppConfigPollResult>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            token.Register(static state =>
                ((TaskCompletionSource<AppConfigPollResult>)state!).TrySetCanceled(),
                completion
            );
            return completion.Task;
        };
        var resource = new AwsAppConfigResource(
            client,
            "app",
            "env",
            "profile",
            delayAsync: (_, cancellationToken) =>
                Task.Delay(TimeSpan.FromHours(1), cancellationToken)
        );

        var wait = resource.WaitForChangeAsync(null).AsTask();
        resource.Dispose();

        Should.Throw<ObjectDisposedException>(() => wait.GetAwaiter().GetResult());
        Should.Throw<ObjectDisposedException>(() => resource.ReadAsync().GetAwaiter().GetResult());
        resource.Dispose();
    }

    [Test]
    public async Task DirectMode_FlowsThroughCodecPipelineToTypedState()
    {
        var client = new FakeAppConfigDataClient();
        client.EnqueueData("t1", SettingsV1, TimeSpan.FromMilliseconds(10), "v1");
        client.EnqueueData("t2", SettingsV1, TimeSpan.FromMilliseconds(10), "v1");
        using var resource = CreateDirectResource(client);
        var source = SerializedStateSource.FromResource<AppSettings.Fragment>(
            "appconfig",
            resource,
            new JsonStateCodec<AppSettings.Fragment>()
        );

        source.Watcher.ShouldNotBeNull();
        var result = await source.Reader.ReadAsync();

        result.Status.ShouldBe(StateReadStatus.Success);
        result.Value!.RetryCount.Value.ShouldBe(7);
        result.Value.Label.Value.ShouldBe("from-appconfig");
        var runtime = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([source])
        );
        (await runtime.ReadAsync()).Value!.Label.ShouldBe("from-appconfig");
    }

    [Test]
    public async Task MalformedPayload_SurfacesInvalidPayloadWithoutThrowing()
    {
        var client = new FakeAppConfigDataClient();
        client.EnqueueData("t1", "null", TimeSpan.FromMilliseconds(10), "v1");
        client.EnqueueData("t2", "{oops", TimeSpan.FromMilliseconds(10), "v2");
        using var resource = CreateDirectResource(client);
        var source = SerializedStateSource.FromResource<AppSettings.Fragment>(
            "appconfig",
            resource,
            new JsonStateCodec<AppSettings.Fragment>()
        );

        var result = await source.Reader.ReadAsync();

        result.Status.ShouldBe(StateReadStatus.InvalidPayload);
        await Should.ThrowAsync<JsonException>(async () => await source.Reader.ReadAsync());
    }

    [Test]
    public async Task DirectRegistration_EndToEndThroughSdkClient()
    {
        var sdk = new StubAppConfigDataClient();
        sdk.EnqueueStart("sdk-initial");
        sdk.EnqueueData("sdk-next", SettingsV1, 60, "v1");
        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                    sources.FromAwsAppConfig(
                        new AwsAppConfigSourceOptions
                        {
                            Id = "appconfig",
                            ApplicationId = "my-app",
                            EnvironmentId = "production",
                            ConfigurationProfileId = "settings",
                            Client = sdk,
                            Codec = StateCodecBinding.Typed(
                                new JsonStateCodec<AppSettings.Fragment>()
                            ),
                        }
                    )
                )
            );
        });

        var value = await context.GetState<AppSettings>().GetValueAsync();

        value.RetryCount.ShouldBe(7);
        value.Label.ShouldBe("from-appconfig");
        sdk.LastStartRequest!.ApplicationIdentifier.ShouldBe("my-app");
        sdk.LastStartRequest.EnvironmentIdentifier.ShouldBe("production");
        sdk.LastStartRequest.ConfigurationProfileIdentifier.ShouldBe("settings");
        sdk.SeenTokens.ShouldBe(["sdk-initial"]);
    }

    [Test]
    public async Task LayeredSources_FallBackUntilAppConfigHasValue()
    {
        var client = new FakeAppConfigDataClient();
        client.EnqueueData("t1", json: null, TimeSpan.FromMilliseconds(10));
        client.EnqueueData("t2", SettingsV2, TimeSpan.FromMilliseconds(10), "v2");
        using var resource = CreateDirectResource(client);
        var appConfigSource = SerializedStateSource.FromResource<AppSettings.Fragment>(
            "appconfig",
            resource,
            new JsonStateCodec<AppSettings.Fragment>(),
            priority: 100,
            fallbackCondition: StateFallbackCondition.NotFoundOrUnavailable
        );
        var defaults = new StateSource<AppSettings.Fragment>(
            "defaults",
            new InMemoryStateSource<AppSettings.Fragment>(
                new AppSettings.Fragment { Label = Optional<string?>.Present("default") }
            )
        );
        await using var runtime = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([appConfigSource, defaults])
        );

        (await runtime.ReadAsync()).Value!.Label.ShouldBe("default");
        (await runtime.ReadAsync()).Value!.Label.ShouldBe("updated");
    }

    [Test]
    public async Task AgentMode_EndToEndMatchesDirectSemantics()
    {
        var handler = new FakeAgentHandler();
        handler.EnqueueJson(SettingsV1, version: "7");
        handler.EnqueueJson(SettingsV1, version: "7");
        using var httpClient = new HttpClient(handler);
        using var resource = new AwsAppConfigResource(
            httpClient,
            "my-app",
            "production",
            "settings"
        );

        resource.Mode.ShouldBe(AwsAppConfigMode.Agent);
        var result = await resource.ReadAsync();

        result.Status.ShouldBe(StateReadStatus.Success);
        result.Revision.ShouldBe("7");
        Encoding.UTF8.GetString(result.Content.ToArray()).ShouldBe(SettingsV1);
        handler.RequestUris.Count.ShouldBe(1);
        handler
            .RequestUris[0]
            .AbsolutePath.ShouldBe("/applications/my-app/environments/production/configurations/settings");

        var source = SerializedStateSource.FromResource<AppSettings.Fragment>(
            "agent",
            resource,
            new JsonStateCodec<AppSettings.Fragment>()
        );
        var typed = await source.Reader.ReadAsync();
        typed.Status.ShouldBe(StateReadStatus.Success);
        typed.Value!.RetryCount.Value.ShouldBe(7);
    }

    [Test]
    public async Task AgentMode_WatcherSignalsOnlyOnChangedBytesAtConfiguredInterval()
    {
        var handler = new FakeAgentHandler();
        handler.EnqueueJson(SettingsV1, version: "7");
        handler.EnqueueJson(SettingsV1, version: "7");
        handler.EnqueueJson(SettingsV2, version: "9");
        handler.EnqueueJson(SettingsV2, version: "9");
        var delays = new RecordingDelay();
        using var httpClient = new HttpClient(handler);
        using var resource = new AwsAppConfigResource(
            httpClient,
            "app",
            "env",
            "profile",
            new AwsAppConfigResourceOptions { AgentPollInterval = TimeSpan.FromMilliseconds(17) }
        );
        resource.DelayAsync = delays.InvokeAsync;
        var initial = await resource.ReadAsync();
        initial.Revision.ShouldBe("7");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await resource.WaitForChangeAsync("7", timeout.Token);

        delays.Recorded.ShouldBe(
            [TimeSpan.FromMilliseconds(17), TimeSpan.FromMilliseconds(17)]
        );
        handler.LastEntityId.ShouldBeNull();
        (await resource.ReadAsync(timeout.Token)).Revision.ShouldBe("9");
    }

    [Test]
    public async Task AgentMode_MissingConfigurationMapsToNotFoundAndRetainsState()
    {
        var handler = new FakeAgentHandler();
        handler.EnqueueJson(SettingsV1, version: "7");
        handler.EnqueueMissing();
        handler.EnqueueMissing();
        using var httpClient = new HttpClient(handler);
        using var resource = new AwsAppConfigResource(httpClient, "app", "env", "profile");

        (await resource.ReadAsync()).Status.ShouldBe(StateReadStatus.Success);
        var retained = await resource.ReadAsync();

        retained.Status.ShouldBe(StateReadStatus.Success);
        retained.Revision.ShouldBe("7");
    }

    [Test]
    public async Task AgentMode_TransientFailureRetainsStateAndSurfacesHealth()
    {
        var handler = new FakeAgentHandler();
        handler.EnqueueJson(SettingsV1, version: "7");
        handler.EnqueueStatus(HttpStatusCode.InternalServerError);
        using var httpClient = new HttpClient(handler);
        using var resource = new AwsAppConfigResource(httpClient, "app", "env", "profile");
        await resource.ReadAsync();

        var result = await resource.ReadAsync();

        result.Status.ShouldBe(StateReadStatus.Success);
        result.Revision.ShouldBe("7");
        resource.GetHealthSnapshot().LastError.ShouldNotBeNull();
    }

    [Test]
    public async Task AgentRegistration_EndToEndThroughConfiglueApp()
    {
        var handler = new FakeAgentHandler();
        handler.EnqueueJson(SettingsV1, version: "7");
        using var httpClient = new HttpClient(handler);
        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                    sources.FromAwsAppConfigAgent(
                        new AwsAppConfigAgentSourceOptions
                        {
                            Id = "agent",
                            ApplicationId = "my-app",
                            EnvironmentId = "production",
                            ConfigurationProfileId = "settings",
                            Client = httpClient,
                            Codec = StateCodecBinding.Typed(
                                new JsonStateCodec<AppSettings.Fragment>()
                            ),
                        }
                    )
                )
            );
        });

        var value = await context.GetState<AppSettings>().GetValueAsync();

        value.RetryCount.ShouldBe(7);
        value.Label.ShouldBe("from-appconfig");
    }

    [Test]
    public void ResourceId_IsStablePerIdentifiersAndClient()
    {
        using var first = CreateDirectResource(new FakeAppConfigDataClient());
        using var same = CreateDirectResource(new FakeAppConfigDataClient());
        using var other = new AwsAppConfigResource(
            new FakeAppConfigDataClient(),
            "app",
            "env",
            "other-profile",
            delayAsync: (_, _) => Task.CompletedTask
        );
        using var withClient = new AwsAppConfigResource(
            new FakeAppConfigDataClient(),
            "app",
            "env",
            "profile",
            new AwsAppConfigResourceOptions { ClientId = "worker-a" },
            delayAsync: (_, _) => Task.CompletedTask
        );

        var context = ConfiglueResourceContext.Default;
        same.GetResourceId(context).ShouldBe(first.GetResourceId(context));
        other.GetResourceId(context).ShouldNotBe(first.GetResourceId(context));
        withClient.GetResourceId(context).ShouldNotBe(first.GetResourceId(context));
        first.GetResourceId(context).Value.ShouldStartWith("appconfig:");
    }

    [Test]
    public void Resource_IsReadOnly()
    {
        using var resource = CreateDirectResource(new FakeAppConfigDataClient());
        var source = SerializedStateSource.FromResource<AppSettings.Fragment>(
            "appconfig",
            resource,
            new JsonStateCodec<AppSettings.Fragment>()
        );

        source.Writer.ShouldBeNull();
        source.Watcher.ShouldNotBeNull();
    }

    [Test]
    public void Registration_RejectsInvalidOptions()
    {
        var builder = new ConfiglueSourceSetBuilder();
        var codec = StateCodecBinding.Typed(new JsonStateCodec<AppSettings.Fragment>());

        Should.Throw<ArgumentException>(() =>
            builder.FromAwsAppConfig(
                new AwsAppConfigSourceOptions
                {
                    ApplicationId = "app",
                    EnvironmentId = "env",
                    ConfigurationProfileId = "profile",
                    Client = new StubAppConfigDataClient(),
                    ClientFactory = _ => new StubAppConfigDataClient(),
                    Codec = codec,
                }
            )
        );
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new AwsAppConfigResource(
                new FakeAppConfigDataClient(),
                "app",
                "env",
                "profile",
                new AwsAppConfigResourceOptions { RequiredMinimumPollIntervalInSeconds = 5 }
            )
        );
        Should.Throw<ArgumentException>(() =>
            builder.FromAwsAppConfigAgent(
                new AwsAppConfigAgentSourceOptions
                {
                    ApplicationId = "app",
                    EnvironmentId = "env",
                    ConfigurationProfileId = "profile",
                    Client = new HttpClient(),
                    ClientFactory = _ => new HttpClient(),
                    Codec = codec,
                }
            )
        );
    }

    private static AwsAppConfigResource CreateDirectResource(FakeAppConfigDataClient client) =>
        new(
            client,
            "app",
            "env",
            "profile",
            delayAsync: (_, _) => Task.CompletedTask
        );

    private sealed class RecordingDelay
    {
        private readonly object _gate = new();
        private readonly List<TimeSpan> _recorded = [];

        public IReadOnlyList<TimeSpan> Recorded
        {
            get
            {
                lock (_gate)
                {
                    return _recorded.ToArray();
                }
            }
        }

        public Task InvokeAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _recorded.Add(delay);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakeAppConfigDataClient : IAwsAppConfigDataClient
    {
        private readonly object _gate = new();
        private readonly Queue<object> _pollQueue = new();
        private readonly List<string> _seenTokens = [];
        private TimeSpan _lastInterval = TimeSpan.FromMilliseconds(10);
        private int _autoToken;

        public int StartCount { get; private set; }

        public string? LastApplicationId { get; private set; }

        public string? LastEnvironmentId { get; private set; }

        public string? LastConfigurationProfileId { get; private set; }

        public int? LastRequiredMinimumPollIntervalInSeconds { get; private set; }

        public string NextStartToken { get; set; } = "initial";

        /// <summary>
        /// When true, an exhausted queue yields endless no-change polls reusing the last
        /// interval, modeling a service with no new configuration.
        /// </summary>
        public bool RepeatEmptyWhenExhausted { get; set; }

        public Func<string, CancellationToken, Task<AppConfigPollResult>>? PollAsyncBehavior
        {
            get;
            set;
        }

        public IReadOnlyList<string> SeenTokens
        {
            get
            {
                lock (_gate)
                {
                    return _seenTokens.ToArray();
                }
            }
        }

        public void EnqueueData(
            string nextToken,
            string? json,
            TimeSpan interval,
            string? version = null
        )
        {
            ReadOnlyMemory<byte> content =
                json is null ? default : Encoding.UTF8.GetBytes(json);
            lock (_gate)
            {
                _lastInterval = interval;
                _pollQueue.Enqueue(
                    new AppConfigPollResult(content, nextToken, interval, "application/json", version)
                );
            }
        }

        public void EnqueueFailure(Exception exception)
        {
            lock (_gate)
            {
                _pollQueue.Enqueue(exception);
            }
        }

        public Task<string> StartSessionAsync(
            string applicationId,
            string environmentId,
            string configurationProfileId,
            int? requiredMinimumPollIntervalInSeconds,
            CancellationToken cancellationToken
        )
        {
            lock (_gate)
            {
                StartCount++;
                LastApplicationId = applicationId;
                LastEnvironmentId = environmentId;
                LastConfigurationProfileId = configurationProfileId;
                LastRequiredMinimumPollIntervalInSeconds = requiredMinimumPollIntervalInSeconds;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(NextStartToken);
        }

        public Task<AppConfigPollResult> GetLatestAsync(
            string configurationToken,
            CancellationToken cancellationToken
        )
        {
            lock (_gate)
            {
                _seenTokens.Add(configurationToken);
            }

            if (PollAsyncBehavior is { } behavior)
            {
                return behavior(configurationToken, cancellationToken);
            }

            object? next;
            TimeSpan repeatInterval;
            lock (_gate)
            {
                if (_pollQueue.Count == 0)
                {
                    if (!RepeatEmptyWhenExhausted)
                    {
                        throw new InvalidOperationException("No queued AppConfig poll response.");
                    }

                    next = null;
                    repeatInterval = _lastInterval;
                }
                else
                {
                    next = _pollQueue.Dequeue();
                    repeatInterval = TimeSpan.Zero;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (next is null)
            {
                var auto = Interlocked.Increment(ref _autoToken);
                return Task.FromResult(
                    new AppConfigPollResult(
                        default,
                        $"auto-{auto}",
                        repeatInterval,
                        "application/json",
                        null
                    )
                );
            }

            return next is Exception exception
                ? Task.FromException<AppConfigPollResult>(exception)
                : Task.FromResult((AppConfigPollResult)next);
        }

        public async Task WaitForPollCountAsync(int expectedCount)
        {
            while (true)
            {
                lock (_gate)
                {
                    if (_seenTokens.Count >= expectedCount)
                    {
                        return;
                    }
                }

                await Task.Delay(5).ConfigureAwait(false);
            }
        }
    }

    private sealed class StubAppConfigDataClient : IAmazonAppConfigData
    {
        private readonly Queue<object> _outcomes = new();
        private readonly List<string> _seenTokens = [];

        public StartConfigurationSessionRequest? LastStartRequest { get; private set; }

        public IReadOnlyList<string> SeenTokens
        {
            get
            {
                lock (_outcomes)
                {
                    return _seenTokens.ToArray();
                }
            }
        }

        public void EnqueueStart(string initialToken) =>
            _outcomes.Enqueue(new StartConfigurationSessionResponse
            {
                InitialConfigurationToken = initialToken,
            });

        public void EnqueueData(string nextToken, string json, int intervalSeconds, string version)
        {
            _outcomes.Enqueue(
                new GetLatestConfigurationResponse
                {
                    Configuration = new MemoryStream(Encoding.UTF8.GetBytes(json)),
                    ContentType = "application/json",
                    NextPollConfigurationToken = nextToken,
                    NextPollIntervalInSeconds = intervalSeconds,
                    VersionLabel = version,
                }
            );
        }

        public void Dispose() { }

        public IClientConfig Config => throw new NotSupportedException();

        // The .NET Framework SDK assembly additionally declares synchronous operations.
        // These extra members are inert on modern targets and scripted on net48.
        public GetLatestConfigurationResponse GetLatestConfiguration(
            GetLatestConfigurationRequest request
        )
        {
            lock (_outcomes)
            {
                _seenTokens.Add(request.ConfigurationToken);
            }

            var outcome = Dequeue();
            if (outcome is Exception exception)
            {
                throw exception;
            }

            return (GetLatestConfigurationResponse)outcome;
        }

        public StartConfigurationSessionResponse StartConfigurationSession(
            StartConfigurationSessionRequest request
        )
        {
            LastStartRequest = request;
            var outcome = Dequeue();
            if (outcome is Exception exception)
            {
                throw exception;
            }

            return (StartConfigurationSessionResponse)outcome;
        }

        public Endpoint DetermineServiceOperationEndpoint(AmazonWebServiceRequest request) =>
            throw new NotSupportedException();

        public Task<StartConfigurationSessionResponse> StartConfigurationSessionAsync(
            StartConfigurationSessionRequest request,
            CancellationToken cancellationToken = default
        )
        {
            LastStartRequest = request;
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = Dequeue();
            return outcome is Exception exception
                ? Task.FromException<StartConfigurationSessionResponse>(exception)
                : Task.FromResult((StartConfigurationSessionResponse)outcome);
        }

        public Task<GetLatestConfigurationResponse> GetLatestConfigurationAsync(
            GetLatestConfigurationRequest request,
            CancellationToken cancellationToken = default
        )
        {
            lock (_outcomes)
            {
                _seenTokens.Add(request.ConfigurationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var outcome = Dequeue();
            return outcome is Exception exception
                ? Task.FromException<GetLatestConfigurationResponse>(exception)
                : Task.FromResult((GetLatestConfigurationResponse)outcome);
        }

        private object Dequeue()
        {
            lock (_outcomes)
            {
                if (_outcomes.Count == 0)
                {
                    throw new InvalidOperationException("No queued SDK response.");
                }

                return _outcomes.Dequeue();
            }
        }
    }

    private sealed class FakeAgentHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();
        private readonly List<Uri> _requestUris = [];
        private string? _lastEntityId;

        public IReadOnlyList<Uri> RequestUris
        {
            get
            {
                lock (_responses)
                {
                    return _requestUris.ToArray();
                }
            }
        }

        public string? LastEntityId
        {
            get
            {
                lock (_responses)
                {
                    return _lastEntityId;
                }
            }
        }

        public void EnqueueJson(string json, string? version = null)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            if (version is not null)
            {
                response.Headers.TryAddWithoutValidation("Configuration-Version", version);
            }

            _responses.Enqueue(response);
        }

        public void EnqueueMissing() =>
            _responses.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound));

        public void EnqueueStatus(HttpStatusCode status) =>
            _responses.Enqueue(new HttpResponseMessage(status));

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_responses)
            {
                _requestUris.Add(request.RequestUri!);
                if (request.Headers.TryGetValues("Entity-Id", out var values))
                {
                    _lastEntityId = string.Join(",", values);
                }

                if (_responses.Count == 0)
                {
                    throw new InvalidOperationException("No queued agent response.");
                }

                return Task.FromResult(_responses.Dequeue());
            }
        }
    }
}
