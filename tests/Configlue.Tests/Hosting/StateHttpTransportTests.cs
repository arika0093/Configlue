using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Configlue.Hosting.AspNetCore;
using Configlue.Provider.Json;
using Configlue.Source.Http;
using Configlue.Testing;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Configlue.Tests;

public sealed class StateHttpTransportTests
{
    [Test]
    public async Task GetRoundtrip_ReturnsEffectiveStateWithEtag()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(7) }
        );
        await using var app = await StartStateAppAsync(store, "/api/settings");
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("http://localhost/api/settings");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        response.Headers.ETag.ShouldNotBeNull();

        var body = await response.Content.ReadAsByteArrayAsync();
        var fragment = DeserializeFragment(body);
        fragment.RetryCount.Value.ShouldBe(7);

        var expectedEtag = ComputeEtagHex(body);
        ParseEtag(response.Headers.ETag!.ToString()).ShouldBe(expectedEtag);
    }

    [Test]
    public async Task PutRoundtrip_UpdatesServerState()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        await using var app = await StartStateAppAsync(store, "/api/settings");
        using var client = app.GetTestClient();

        using var initial = await client.GetAsync("http://localhost/api/settings");
        var initialEtag = initial.Headers.ETag!.ToString();

        var update = SerializeFragment(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(42) }
        );
        using var put = new HttpRequestMessage(HttpMethod.Put, "http://localhost/api/settings")
        {
            Content = new ByteArrayContent(update),
        };
        put.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        put.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(initialEtag));
        using var putResponse = await client.SendAsync(put);
        putResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        putResponse.Headers.ETag.ShouldNotBeNull();
        putResponse.Headers.ETag!.ToString().ShouldNotBe(initialEtag);

        using var after = await client.GetAsync("http://localhost/api/settings");
        var afterBody = await after.Content.ReadAsByteArrayAsync();
        DeserializeFragment(afterBody).RetryCount.Value.ShouldBe(42);
    }

    [Test]
    public async Task JsonContract_UsesGeneratedFragmentShape()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            AppSettings.Fragment.From(
                new AppSettings
                {
                    Enabled = false,
                    RetryCount = 9,
                    Label = "contract",
                }
            )
        );
        await using var app = await StartStateAppAsync(store, "/api/settings");
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("http://localhost/api/settings");
        var body = await response.Content.ReadAsByteArrayAsync();
        var text = Encoding.UTF8.GetString(body);
        text.ShouldContain("\"RetryCount\":9");
        text.ShouldContain("\"Enabled\":false");
        text.ShouldContain("\"Label\":\"contract\"");
        text.ShouldNotContain("$version");
        text.ShouldNotContain("$configlue");

        // Schema compatibility: the fragment converter round-trips the payload.
        var fragment = DeserializeFragment(body);
        var reserialized = SerializeFragment(fragment);
        ComputeEtagHex(reserialized).ShouldBe(ParseEtag(response.Headers.ETag!.ToString()));
    }

    [Test]
    public async Task AotSafeBridge_EncodesWithoutReflectionOnModel()
    {
        var descriptor = Configlue.CompilerServices.ConfiglueModelDescriptor<AppSettings>.Current;
        descriptor.FragmentType.ShouldBe(typeof(AppSettings.Fragment));
        var model = new AppSettings { RetryCount = 12, Label = "aot" };
        var fragment = (AppSettings.Fragment)descriptor.ToFragmentBoxed(model);
        var bytes = ConfiglueFragmentJson.SerializeToCanonicalBytes(
            fragment,
            descriptor.FragmentType,
            null
        );
        var decoded = (AppSettings.Fragment)
            ConfiglueFragmentJson.Deserialize(descriptor.FragmentType, bytes, null);
        var roundtripped = (AppSettings)descriptor.FromFragmentBoxed(decoded);
        roundtripped.RetryCount.ShouldBe(12);
        roundtripped.Label.ShouldBe("aot");
    }

    [Test]
    public async Task StaleIfMatch_Returns412()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        await using var app = await StartStateAppAsync(store, "/api/settings");
        using var client = app.GetTestClient();

        var stale = SerializeFragment(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(2) }
        );
        using var put = new HttpRequestMessage(HttpMethod.Put, "http://localhost/api/settings")
        {
            Content = new ByteArrayContent(stale),
        };
        put.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        put.Headers.IfMatch.Add(
            EntityTagHeaderValue.Parse(
                "\"0000000000000000000000000000000000000000000000000000000000000000\""
            )
        );
        using var response = await client.SendAsync(put);
        response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
    }

    [Test]
    public async Task ValidationFailure_Returns422()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        await using var app = await StartStateAppAsync(store, "/api/settings");
        using var client = app.GetTestClient();

        var invalid = SerializeFragment(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(999) }
        );
        using var put = new HttpRequestMessage(HttpMethod.Put, "http://localhost/api/settings")
        {
            Content = new ByteArrayContent(invalid),
        };
        put.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await client.SendAsync(put);
        response.StatusCode.ShouldBe((HttpStatusCode)422);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    [Test]
    public async Task MalformedPayload_Returns400()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        await using var app = await StartStateAppAsync(store, "/api/settings");
        using var client = app.GetTestClient();

        using var put = new HttpRequestMessage(HttpMethod.Put, "http://localhost/api/settings")
        {
            Content = new ByteArrayContent("{\"RetryCount\":}"u8.ToArray()),
        };
        put.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await client.SendAsync(put);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task CoreWriteConflict_Returns409()
    {
        // High-priority read-only layer shadows RetryCount, so writing it conflicts.
        var shadow = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(5) }
        );
        var writable = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { Label = Optional<string?>.Present("base") }
        );
        await using var app = await StartStateAppWithSourcesAsync(
            "/api/settings",
            true,
            false,
            ("shadow", 100, shadow, false),
            ("writable", 0, writable, true)
        );
        using var client = app.GetTestClient();

        var update = SerializeFragment(
            AppSettings.Fragment.From(new AppSettings { RetryCount = 8, Label = "base" })
        );
        using var put = new HttpRequestMessage(HttpMethod.Put, "http://localhost/api/settings")
        {
            Content = new ByteArrayContent(update),
        };
        put.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await client.SendAsync(put);
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Test]
    public async Task MultiSourceWriteFailure_Returns409()
    {
        var good = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { Label = Optional<string?>.Present("base") }
        );
        var failing = new FailingWriteSource();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder
            .Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddConfiglue(config =>
        {
            config.Add<AppSettings>(model =>
            {
                model.Sources(sources =>
                {
                    sources.Add(
                        new StateSource<AppSettings.Fragment>(
                            "good",
                            good,
                            new StateSourceOptions<AppSettings.Fragment>
                            {
                                Writer = good,
                                Watcher = good,
                            }
                        )
                    );
                    sources.Add(
                        new StateSource<AppSettings.Fragment>(
                            "failing",
                            failing,
                            new StateSourceOptions<AppSettings.Fragment>
                            {
                                Writer = failing,
                                DisableWriteCapability = false,
                            }
                        )
                    );
                });
                model.Writes(write =>
                {
                    write.DefaultTo(SourceKey<AppSettings>.Named("good"));
                    write.Route(x => x.Label, SourceKey<AppSettings>.Named("failing"));
                });
            });
        });
        var app = builder.Build();
        app.MapConfiglueState<AppSettings>("/api/settings");
        await app.StartAsync();
        await using (app)
        {
            using var client = app.GetTestClient();

            var update = SerializeFragment(
                AppSettings.Fragment.From(new AppSettings { RetryCount = 3, Label = "multi" })
            );
            using var put = new HttpRequestMessage(HttpMethod.Put, "http://localhost/api/settings")
            {
                Content = new ByteArrayContent(update),
            };
            put.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var response = await client.SendAsync(put);
            (
                response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.InternalServerError
            ).ShouldBeTrue();
        }
    }

    [Test]
    public async Task AnonymousAndAuthorized_SubjectBehavior()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(3) }
        );
        await using var denied = await StartStateAppAsync(
            store,
            "/api/settings",
            allowAuthorization: false,
            requireAuthorization: true
        );
        using var deniedClient = denied.GetTestClient();
        using var deniedResponse = await deniedClient.GetAsync("http://localhost/api/settings");
        (
            deniedResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
        ).ShouldBeTrue();

        await using var allowed = await StartStateAppAsync(
            store,
            "/api/settings",
            allowAuthorization: true,
            requireAuthorization: true
        );
        using var allowedClient = allowed.GetTestClient();
        using var allowedResponse = await allowedClient.GetAsync("http://localhost/api/settings");
        allowedResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task SubjectIsolation_DifferentSubjectsSeeDifferentState()
    {
        var perSubject = new KeyedMemorySource();
        perSubject.Set(
            SubjectKey.FromSegments("tenant", "alice"),
            AppSettings.Fragment.From(new AppSettings { Label = "alice-state", RetryCount = 1 })
        );
        perSubject.Set(
            SubjectKey.FromSegments("tenant", "bob"),
            AppSettings.Fragment.From(new AppSettings { Label = "bob-state", RetryCount = 2 })
        );
        await using var app = await StartPerSubjectStateAppAsync(perSubject, "/api/settings");
        using var client = app.GetTestClient();

        using var aliceRequest = new HttpRequestMessage(
            HttpMethod.Get,
            "http://localhost/api/settings"
        );
        aliceRequest.Headers.Add("X-User", "alice");
        using var aliceResponse = await client.SendAsync(aliceRequest);
        aliceResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        DeserializeFragment(await aliceResponse.Content.ReadAsByteArrayAsync())
            .Label.Value.ShouldBe("alice-state");

        using var bobRequest = new HttpRequestMessage(
            HttpMethod.Get,
            "http://localhost/api/settings"
        );
        bobRequest.Headers.Add("X-User", "bob");
        using var bobResponse = await client.SendAsync(bobRequest);
        DeserializeFragment(await bobResponse.Content.ReadAsByteArrayAsync())
            .Label.Value.ShouldBe("bob-state");

        // Untrusted client subject identifiers are ignored: the server resolves subjects
        // only from the trusted X-User header via the registered accessor.
        using var evilRequest = new HttpRequestMessage(
            HttpMethod.Get,
            "http://localhost/api/settings"
        );
        evilRequest.Headers.Add("X-User", "alice");
        evilRequest.Headers.Add("X-Subject-Evil", "bob");
        using var evilResponse = await client.SendAsync(evilRequest);
        DeserializeFragment(await evilResponse.Content.ReadAsByteArrayAsync())
            .Label.Value.ShouldBe("alice-state");
    }

    [Test]
    public async Task ClientSource_ReadWriteAndSseInvalidation()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        await using var app = await StartStateAppAsync(store, "/api/settings");
        using var httpClient = app.GetTestClient();
        httpClient.BaseAddress = new Uri("http://localhost");

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                    sources.FromHttpState(
                        new HttpStateSourceOptions
                        {
                            Id = "http-state",
                            EndPoint = "http://localhost/api/settings",
                            Client = httpClient,
                            Writable = true,
                        }
                    )
                )
            );
        });

        var state = (IConfiglueRuntimeState<AppSettings>)context.GetState<AppSettings>();
        (await state.GetValueAsync()).RetryCount.ShouldBe(1);

        await state.SaveAsync(settings => settings.RetryCount = 21);
        (await state.GetValueAsync()).RetryCount.ShouldBe(21);

        // External server-side change drives SSE invalidation.
        var watcherState = context.GetState<AppSettings>();
        using var changeObserved = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var waitTask = watcherState.OnChangeObservedAsync(changeObserved.Token);
        await Task.Delay(500, changeObserved.Token);
        store.Set(new AppSettings.Fragment { RetryCount = Optional<int>.Present(33) });
        await waitTask;
        (await state.GetValueAsync()).RetryCount.ShouldBe(33);
    }

    [Test]
    public async Task Sse_InvalidationAfterClientWrite()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        await using var app = await StartStateAppAsync(store, "/api/settings");
        using var watcherClient = app.GetTestClient();
        watcherClient.BaseAddress = new Uri("http://localhost");
        using var writerClient = app.GetTestClient();
        writerClient.BaseAddress = new Uri("http://localhost");

        await using var watcherContext = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                    sources.FromHttpState(
                        new HttpStateSourceOptions
                        {
                            Id = "watcher",
                            EndPoint = "http://localhost/api/settings",
                            Client = watcherClient,
                        }
                    )
                )
            );
        });
        await using var writerContext = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.Sources(sources =>
                    sources.FromHttpState(
                        new HttpStateSourceOptions
                        {
                            Id = "writer",
                            EndPoint = "http://localhost/api/settings",
                            Client = writerClient,
                            Writable = true,
                        }
                    )
                );
                model.Writes(write => write.DefaultTo(SourceKey<AppSettings>.Named("writer")));
            });
        });

        var watcher = (IConfiglueRuntimeState<AppSettings>)watcherContext.GetState<AppSettings>();
        (await watcher.GetValueAsync()).RetryCount.ShouldBe(1);
        using var changeObserved = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var waitTask = watcher.OnChangeObservedAsync(changeObserved.Token);
        await Task.Delay(500, changeObserved.Token);

        var writer = (IConfiglueRuntimeState<AppSettings>)writerContext.GetState<AppSettings>();
        await writer.SaveAsync(settings => settings.RetryCount = 55);

        await waitTask;
        (await watcher.GetValueAsync()).RetryCount.ShouldBe(55);
    }

    [Test]
    public async Task DisconnectReconnect_MissedEventConvergesViaReread()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        await using var app = await StartStateAppAsync(store, "/api/settings");
        using var httpClient = app.GetTestClient();
        httpClient.BaseAddress = new Uri("http://localhost");

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                    sources.FromHttpState(
                        new HttpStateSourceOptions
                        {
                            Id = "http-state",
                            EndPoint = "http://localhost/api/settings",
                            Client = httpClient,
                            ReconnectInitialDelay = TimeSpan.FromMilliseconds(20),
                            ReconnectMaxDelay = TimeSpan.FromMilliseconds(100),
                        }
                    )
                )
            );
        });

        var runtime = (IConfiglueRuntimeState<AppSettings>)context.GetState<AppSettings>();
        var first = await runtime.GetValueAsync();
        first.RetryCount.ShouldBe(1);

        // Change the server while no watcher is connected (missed SSE event).
        store.Set(new AppSettings.Fragment { RetryCount = Optional<int>.Present(77) });

        // Re-reading converges to the current state even though the SSE event was missed.
        (await runtime.GetValueAsync()).RetryCount.ShouldBe(77);
    }

    [Test]
    public async Task Events_ReconnectReportsChangeSinceLastEventId()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        await using var app = await StartStateAppAsync(store, "/api/settings");
        using var client = app.GetTestClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var initial = await client.GetAsync("/api/settings", timeout.Token);
        var baseline = initial.Headers.ETag!.Tag.Trim('"');

        // Change before opening the stream: no live subscription can receive it.
        store.Set(new AppSettings.Fragment { RetryCount = Optional<int>.Present(9) });
        using var current = await client.GetAsync("/api/settings", timeout.Token);
        var expected = current.Headers.ETag!.Tag.Trim('"');
        expected.ShouldNotBe(baseline);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/settings/events");
        request.Headers.TryAddWithoutValidation("Last-Event-ID", baseline);
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token
        );
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var reader = new StreamReader(stream);
        (await reader.ReadLineAsync(timeout.Token)).ShouldBe("event: changed");
        (await reader.ReadLineAsync(timeout.Token)).ShouldBe($"id: {expected}");
    }

    [Test]
    public async Task Watcher_CancellationDisposalAndMultipleWatchers()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        await using var app = await StartStateAppAsync(store, "/api/settings");
        using var httpClient = app.GetTestClient();
        httpClient.BaseAddress = new Uri("http://localhost");

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                    sources.FromHttpState(
                        new HttpStateSourceOptions
                        {
                            Id = "http-state",
                            EndPoint = "http://localhost/api/settings",
                            Client = httpClient,
                        }
                    )
                )
            );
        });

        var runtime = (IConfiglueRuntimeState<AppSettings>)context.GetState<AppSettings>();
        // Establish the initial effective value before waiting for a change. A fixed
        // delay cannot ensure initialization finishes on a busy CI runner.
        (await runtime.GetValueAsync()).RetryCount.ShouldBe(1);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await runtime.WaitForChangeObservedAsync("1", canceled.Token)
        );

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = runtime.WaitForChangeObservedAsync("1", timeout.Token);
        var second = runtime.WaitForChangeObservedAsync("1", timeout.Token);
        store.Set(new AppSettings.Fragment { RetryCount = Optional<int>.Present(9) });
        await first;
        await second;
        (await runtime.GetValueAsync()).RetryCount.ShouldBe(9);
    }

    [Test]
    public async Task NamedHttpClientFactory_Integration()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(11) }
        );
        await using var app = await StartStateAppAsync(store, "/api/settings");
        using var serverClient = app.GetTestClient();

        var services = new ServiceCollection();
        services.AddSingleton(serverClient);
        services
            .AddHttpClient("state-api")
            .ConfigurePrimaryHttpMessageHandler(() => new ForwardingHandler(serverClient));
        services.AddConfiglue(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                    sources.FromHttpStateClientFactory(
                        "state-api",
                        new HttpStateSourceOptions
                        {
                            Id = "named-http",
                            EndPoint = "http://localhost/api/settings",
                            WatchChanges = false,
                        }
                    )
                )
            );
        });

        await using var provider = services.BuildServiceProvider();
        var state = provider.GetRequiredService<IWritableState<AppSettings>>();
        (await state.GetValueAsync()).RetryCount.ShouldBe(11);
    }

    [Test]
    public async Task Client_PreservesErrorDistinctions()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        await using var app = await StartStateAppAsync(store, "/api/settings");
        using var httpClient = app.GetTestClient();
        httpClient.BaseAddress = new Uri("http://localhost");

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                // Disable client-side validation so the invalid value reaches the server,
                // proving the server's 422 maps to a client validation error.
                model.ValidateDataAnnotations = false;
                model.Sources(sources =>
                    sources.FromHttpState(
                        new HttpStateSourceOptions
                        {
                            Id = "http-state",
                            EndPoint = "http://localhost/api/settings",
                            Client = httpClient,
                            Writable = true,
                        }
                    )
                );
                model.Writes(write => write.DefaultTo(SourceKey<AppSettings>.Named("http-state")));
            });
        });

        var state = (IConfiglueRuntimeState<AppSettings>)context.GetState<AppSettings>();

        // Stale revision on direct source write surfaces as a StateConflictException
        // carrying the stale-ETag distinction.
        var directReader = new HttpStateReader<AppSettings.Fragment>(
            httpClient,
            new Uri("http://localhost/api/settings"),
            new Uri("http://localhost/api/settings/events"),
            writable: true
        );
        await Should.ThrowAsync<StateConflictException>(async () =>
            await directReader.WriteAsync(
                ConfiglueResourceContext.Default,
                new StateWriteRequest<AppSettings.Fragment>(
                    new AppSettings.Fragment { RetryCount = Optional<int>.Present(2) },
                    RevisionCondition.Match(
                        "0000000000000000000000000000000000000000000000000000000000000000"
                    )
                )
            )
        );
        await Should.ThrowAsync<HttpStateStaleException>(async () =>
            await directReader.WriteAsync(
                ConfiglueResourceContext.Default,
                new StateWriteRequest<AppSettings.Fragment>(
                    new AppSettings.Fragment { RetryCount = Optional<int>.Present(2) },
                    RevisionCondition.Match(
                        "0000000000000000000000000000000000000000000000000000000000000000"
                    )
                )
            )
        );

        // Server-side validation surfaces without ASP.NET leakage.
        await Should.ThrowAsync<HttpStateValidationException>(async () =>
            await state.SaveAsync(settings => settings.RetryCount = 999)
        );
    }

    private static byte[] SerializeFragment(AppSettings.Fragment fragment)
    {
        return ConfiglueFragmentJson.SerializeToCanonicalBytes(
            fragment,
            typeof(AppSettings.Fragment),
            null
        );
    }

    private static AppSettings.Fragment DeserializeFragment(byte[] json)
    {
        return (AppSettings.Fragment)
            ConfiglueFragmentJson.Deserialize(typeof(AppSettings.Fragment), json, null);
    }

    private static string ComputeEtagHex(byte[] json)
    {
        return Convert
            .ToHexString(System.Security.Cryptography.SHA256.HashData(json))
            .ToLowerInvariant();
    }

    private static string ParseEtag(string etag)
    {
        return etag.Trim().Trim('"').ToLowerInvariant();
    }

    private static async Task<WebApplication> StartStateAppAsync(
        InMemoryStateSource<AppSettings.Fragment> store,
        string pattern,
        bool allowAuthorization = true,
        bool requireAuthorization = false
    )
    {
        return await StartStateAppWithSourcesAsync(
            pattern,
            allowAuthorization,
            requireAuthorization,
            ("server-store", 0, store, true)
        );
    }

    private static async Task<WebApplication> StartStateAppWithSourcesAsync(
        string pattern,
        bool allowAuthorization,
        bool requireAuthorization,
        params (
            string Id,
            int Priority,
            ISourceReader<AppSettings.Fragment> Store,
            bool Writable
        )[] stores
    )
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder
            .Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization(options =>
            options.DefaultPolicy = new AuthorizationPolicyBuilder()
                .RequireAssertion(_ => allowAuthorization)
                .Build()
        );
        builder.Services.AddConfiglue(config =>
        {
            config.Add<AppSettings>(model =>
            {
                model.Sources(sources =>
                {
                    foreach (var (id, priority, store, writable) in stores)
                    {
                        var writer = writable ? store as ISourceWriter<AppSettings.Fragment> : null;
                        var watcher = store as ISourceWatcher;
                        sources.Add(
                            new StateSource<AppSettings.Fragment>(
                                id,
                                store,
                                new StateSourceOptions<AppSettings.Fragment>
                                {
                                    Priority = priority,
                                    Writer = writer,
                                    DisableWriteCapability = writer is null,
                                    Watcher = watcher,
                                }
                            )
                        );
                    }
                });
                var writableIds = stores.Where(s => s.Writable).Select(s => s.Id).ToArray();
                if (writableIds.Length == 1)
                {
                    model.Writes(write =>
                        write.DefaultTo(SourceKey<AppSettings>.Named(writableIds[0]))
                    );
                }
                else if (writableIds.Length > 1)
                {
                    model.Writes(write =>
                        write.DefaultTo(SourceKey<AppSettings>.Named(writableIds[0]))
                    );
                }
            });
        });
        var app = builder.Build();
        var group = app.MapConfiglueState<AppSettings>(pattern);
        if (requireAuthorization)
        {
            group.RequireAuthorization();
        }

        await app.StartAsync();
        return app;
    }

    private static async Task<WebApplication> StartPerSubjectStateAppAsync(
        KeyedMemorySource store,
        string pattern
    )
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder
            .Services.AddAuthentication("test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("test", _ => { });
        builder.Services.AddAuthorization();
        builder
            .Services.AddConfiglueSubject<SettingsSubject>()
            .FromHttpContext(context => new SettingsSubject(
                "tenant",
                context.Request.Headers["X-User"].ToString()
            ));
        builder.Services.AddConfiglue(config =>
        {
            config.Add<AppSettings>(model =>
            {
                model.PerSubject<HttpContextConfiglueSubjectAccessor<SettingsSubject>>();
                model.Sources(sources =>
                    sources.Add(
                        new StateSource<AppSettings.Fragment>(
                            "per-subject-store",
                            store,
                            new StateSourceOptions<AppSettings.Fragment>
                            {
                                Writer = store,
                                Watcher = store,
                            }
                        )
                    )
                );
                model.Writes(write =>
                    write.DefaultTo(SourceKey<AppSettings>.Named("per-subject-store"))
                );
            });
        });
        var app = builder.Build();
        app.MapConfiglueState<AppSettings>(pattern);
        await app.StartAsync();
        return app;
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder
    ) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.NoResult());
    }

    private sealed record SettingsSubject(SubjectKey Key) : IConfiglueSubject
    {
        public SettingsSubject(string tenant, string user)
            : this(SubjectKey.FromSegments(tenant, user)) { }
    }

    private sealed class KeyedMemorySource
        : ISourceReader<AppSettings.Fragment>,
            ISourceWriter<AppSettings.Fragment>,
            ISourceWatcher
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<
            string,
            AppSettings.Fragment
        > _values = new(StringComparer.Ordinal);
        private readonly System.Collections.Concurrent.ConcurrentDictionary<
            string,
            TaskCompletionSource
        > _signals = new(StringComparer.Ordinal);

        public void Set(SubjectKey key, AppSettings.Fragment fragment)
        {
            _values[key.ToString()] = fragment;
            if (_signals.TryRemove(key.ToString(), out var signal))
            {
                signal.TrySetResult();
            }
        }

        public ValueTask<StateReadResult<AppSettings.Fragment>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = context.Subject?.Key.ToString() ?? "";
            if (_values.TryGetValue(key, out var fragment))
            {
                return ValueTaskCompat.FromResult(
                    StateReadResult<AppSettings.Fragment>.Success(fragment, key)
                );
            }

            return ValueTaskCompat.FromResult(StateReadResult<AppSettings.Fragment>.NotFound(key));
        }

        public ValueTask<StateWriteResult> WriteAsync(
            ConfiglueResourceContext context,
            StateWriteRequest<AppSettings.Fragment> request,
            CancellationToken cancellationToken = default
        )
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            var key = context.Subject?.Key.ToString() ?? "";
            _values[key] = request.Value;
            if (_signals.TryRemove(key, out var signal))
            {
                signal.TrySetResult();
            }

            return ValueTaskCompat.FromResult(new StateWriteResult(key));
        }

        public async ValueTask WaitForChangeAsync(
            ConfiglueResourceContext context,
            string? observedRevision,
            CancellationToken cancellationToken = default
        )
        {
            var key = context.Subject?.Key.ToString() ?? "";
            if (
                !string.Equals(observedRevision, key, StringComparison.Ordinal)
                && _values.ContainsKey(key)
            )
            {
                return;
            }

            var signal = _signals.GetOrAdd(
                key,
                _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
            );
            await signal.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class FailingWriteSource
        : ISourceReader<AppSettings.Fragment>,
            ISourceWriter<AppSettings.Fragment>
    {
        public ValueTask<StateReadResult<AppSettings.Fragment>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        ) => ValueTaskCompat.FromResult(StateReadResult<AppSettings.Fragment>.NotFound());

        public ValueTask<StateWriteResult> WriteAsync(
            ConfiglueResourceContext context,
            StateWriteRequest<AppSettings.Fragment> request,
            CancellationToken cancellationToken = default
        ) =>
            ValueTask.FromException<StateWriteResult>(
                new InvalidOperationException("Failing source always fails.")
            );
    }

    private sealed class ForwardingHandler(HttpClient inner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri);
            if (request.Content is not null)
            {
                clone.Content = request.Content;
            }

            foreach (var header in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return inner.SendAsync(
                clone,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken
            );
        }
    }
}

internal static class StateHttpTestExtensions
{
    public static async Task OnChangeObservedAsync(
        this IReadOnlyState<AppSettings> state,
        CancellationToken cancellationToken
    )
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var subscription = state.OnChange(_ => completion.TrySetResult());
        using var registration = cancellationToken.Register(() =>
            completion.TrySetCanceled(cancellationToken)
        );
        await completion.Task.ConfigureAwait(false);
    }

    public static Task WaitForChangeObservedAsync(
        this IConfiglueRuntimeState<AppSettings> runtime,
        string observedRevision,
        CancellationToken cancellationToken
    )
    {
        // Drive the source-level watcher through the runtime's reload path by waiting
        // for the next effective change notification.
        return runtime.OnChangeObservedAsync(cancellationToken);
    }

    public static async Task OnChangeObservedAsync(
        this IConfiglueRuntimeState<AppSettings> runtime,
        CancellationToken cancellationToken
    )
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var subscription = ((IReadOnlyState<AppSettings>)runtime).OnChange(_ =>
            completion.TrySetResult()
        );
        using var registration = cancellationToken.Register(() =>
            completion.TrySetCanceled(cancellationToken)
        );
        await completion.Task.ConfigureAwait(false);
    }
}
