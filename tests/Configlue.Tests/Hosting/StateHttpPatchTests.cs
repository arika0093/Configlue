using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Configlue.Extensibility;
using Configlue.Hosting.AspNetCore;
using Configlue.Provider.Json;
using Configlue.Source.Http;
using Configlue.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Configlue.Tests;

public sealed class StateHttpPatchTests
{
    [Test]
    public async Task PatchReplaceScalar_RoundtripsWithNewEtag()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        await using var app = await StartPatchAppAsync(
            "/api/settings",
            model =>
            {
                model.Sources(sources =>
                    sources.Add(
                        new StateSource<AppSettings.Fragment>(
                            "server-store",
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
                    write.DefaultTo(SourceKey<AppSettings>.Named("server-store"))
                );
            }
        );
        using var client = app.GetTestClient();

        var initialEtag = await GetEtagAsync(client, "http://localhost/api/settings");

        using var patch = PatchRequest(
            "http://localhost/api/settings",
            """[{"op":"replace","path":"/RetryCount","value":42}]""",
            initialEtag
        );
        using var response = await client.SendAsync(patch);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues("Accept-Patch").ShouldContain("application/json-patch+json");
        var newEtag = response.Headers.ETag!.ToString();
        newEtag.ShouldNotBe(initialEtag);

        var body = await response.Content.ReadAsByteArrayAsync();
        DeserializeFragment(body).RetryCount.Value.ShouldBe(42);

        using var after = await client.GetAsync("http://localhost/api/settings");
        (await after.Content.ReadAsByteArrayAsync()).ShouldBe(body);
        after.Headers.ETag!.ToString().ShouldBe(newEtag);
    }

    [Test]
    public async Task AcceptPatch_AdvertisedOnGetPutAndPatch()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        await using var app = await StartSingleStoreAppAsync(store, "/api/settings");
        using var client = app.GetTestClient();

        using var get = await client.GetAsync("http://localhost/api/settings");
        get.Headers.GetValues("Accept-Patch").ShouldContain("application/json-patch+json");

        var etag = get.Headers.ETag!.ToString();
        var update = SerializeFragment(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(2) }
        );
        using var put = new HttpRequestMessage(HttpMethod.Put, "http://localhost/api/settings")
        {
            Content = new ByteArrayContent(update),
        };
        put.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        put.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        using var putResponse = await client.SendAsync(put);
        putResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        putResponse.Headers.GetValues("Accept-Patch").ShouldContain("application/json-patch+json");

        using var options = new HttpRequestMessage(
            HttpMethod.Options,
            "http://localhost/api/settings"
        );
        using var optionsResponse = await client.SendAsync(options);
        optionsResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        optionsResponse
            .Headers.GetValues("Accept-Patch")
            .ShouldContain("application/json-patch+json");
        var allow = string.Join(",", optionsResponse.Content.Headers.GetValues("Allow"));
        allow.ShouldContain("PATCH");
        allow.ShouldContain("OPTIONS");
    }

    [Test]
    public async Task MissingIfMatch_Returns428()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        await using var app = await StartSingleStoreAppAsync(store, "/api/settings");
        using var client = app.GetTestClient();

        using var patch = new HttpRequestMessage(
            new HttpMethod("PATCH"),
            "http://localhost/api/settings"
        )
        {
            Content = new ByteArrayContent(
                """[{"op":"replace","path":"/RetryCount","value":2}]"""u8.ToArray()
            ),
        };
        patch.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json-patch+json");
        using var response = await client.SendAsync(patch);
        response.StatusCode.ShouldBe((HttpStatusCode)428);

        DeserializeFragment(await GetBodyAsync(client)).RetryCount.Value.ShouldBe(1);
    }

    [Test]
    public async Task StaleIfMatch_Returns412WithoutWriting()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        await using var app = await StartSingleStoreAppAsync(store, "/api/settings");
        using var client = app.GetTestClient();

        using var patch = PatchRequest(
            "http://localhost/api/settings",
            """[{"op":"replace","path":"/RetryCount","value":2}]""",
            "\"0000000000000000000000000000000000000000000000000000000000000000\""
        );
        using var response = await client.SendAsync(patch);
        response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);

        DeserializeFragment(await GetBodyAsync(client)).RetryCount.Value.ShouldBe(1);
    }

    [Test]
    public async Task IfMatchStar_Returns400()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        await using var app = await StartSingleStoreAppAsync(store, "/api/settings");
        using var client = app.GetTestClient();

        using var patch = PatchRequest(
            "http://localhost/api/settings",
            """[{"op":"replace","path":"/RetryCount","value":2}]""",
            "*"
        );
        using var response = await client.SendAsync(patch);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task MalformedPatchDocument_Returns400()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        await using var app = await StartSingleStoreAppAsync(store, "/api/settings");
        using var client = app.GetTestClient();
        var etag = await GetEtagAsync(client, "http://localhost/api/settings");

        // Not JSON at all.
        using (var bad = PatchRequest("http://localhost/api/settings", """{"op":}""", etag))
        using (var response = await client.SendAsync(bad))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        // Unknown operation.
        using (
            var bad = PatchRequest(
                "http://localhost/api/settings",
                """[{"op":"frob","path":"/RetryCount","value":2}]""",
                etag
            )
        )
        using (var response = await client.SendAsync(bad))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        // Malformed JSON Pointer (must start with '/' or be empty).
        using (
            var bad = PatchRequest(
                "http://localhost/api/settings",
                """[{"op":"remove","path":"RetryCount"}]""",
                etag
            )
        )
        using (var response = await client.SendAsync(bad))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        // Wrong media type.
        using (
            var wrongType = new HttpRequestMessage(
                new HttpMethod("PATCH"),
                "http://localhost/api/settings"
            )
            {
                Content = new ByteArrayContent(
                    """[{"op":"replace","path":"/RetryCount","value":2}]"""u8.ToArray()
                ),
            }
        )
        {
            wrongType.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            wrongType.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
            using var response = await client.SendAsync(wrongType);
            response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
        }

        DeserializeFragment(await GetBodyAsync(client)).RetryCount.Value.ShouldBe(1);
    }

    [Test]
    public async Task TestOpFailure_Returns409WithoutWriting()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        await using var app = await StartSingleStoreAppAsync(store, "/api/settings");
        using var client = app.GetTestClient();
        var etag = await GetEtagAsync(client, "http://localhost/api/settings");

        using var patch = PatchRequest(
            "http://localhost/api/settings",
            """[{"op":"test","path":"/RetryCount","value":999},{"op":"replace","path":"/RetryCount","value":2}]""",
            etag
        );
        using var response = await client.SendAsync(patch);
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // The failed test aborted before any write: the replace never applied.
        DeserializeFragment(await GetBodyAsync(client)).RetryCount.Value.ShouldBe(1);
    }

    [Test]
    public async Task RemoveFixedProperty_NormalizesThroughModelDefaults()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            AppSettings.Fragment.From(new AppSettings { RetryCount = 7, Label = "keep" })
        );
        await using var app = await StartSingleStoreAppAsync(store, "/api/settings");
        using var client = app.GetTestClient();
        var etag = await GetEtagAsync(client, "http://localhost/api/settings");

        // Document-level remove is canonicalized through the model: RetryCount reappears
        // with its model default instead of unsetting a source contribution.
        using var patch = PatchRequest(
            "http://localhost/api/settings",
            """[{"op":"remove","path":"/RetryCount"}]""",
            etag
        );
        using var response = await client.SendAsync(patch);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var after = await client.GetAsync("http://localhost/api/settings");
        var text = Encoding.UTF8.GetString(await after.Content.ReadAsByteArrayAsync());
        text.ShouldContain("\"RetryCount\":3");
        text.ShouldContain("\"Label\":\"keep\"");
    }

    [Test]
    public async Task ReplaceWithNull_Versus_Remove()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            AppSettings.Fragment.From(new AppSettings { Label = "initial" })
        );
        await using var app = await StartSingleStoreAppAsync(store, "/api/settings");
        using var client = app.GetTestClient();

        var etag = await GetEtagAsync(client, "http://localhost/api/settings");
        using (
            var patch = PatchRequest(
                "http://localhost/api/settings",
                """[{"op":"replace","path":"/Label","value":null}]""",
                etag
            )
        )
        using (var response = await client.SendAsync(patch))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var nulledText = Encoding.UTF8.GetString(await GetBodyAsync(client));
        nulledText.ShouldContain("\"Label\":null");

        etag = await GetEtagAsync(client, "http://localhost/api/settings");
        using (
            var patch = PatchRequest(
                "http://localhost/api/settings",
                """[{"op":"remove","path":"/Label"}]""",
                etag
            )
        )
        using (var response = await client.SendAsync(patch))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // Absent normalizes back to the model default; present-null stays null.
        var removedText = Encoding.UTF8.GetString(await GetBodyAsync(client));
        removedText.ShouldContain("\"Label\":\"default\"");
    }

    [Test]
    public async Task NestedCollectionAndCopyOps_Roundtrip()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            AppSettings.Fragment.From(
                new AppSettings
                {
                    Enabled = true,
                    RetryCount = 1,
                    Label = "ops",
                    Plugins = ["a", "b"],
                }
            )
        );
        await using var app = await StartSingleStoreAppAsync(store, "/api/settings");
        using var client = app.GetTestClient();

        // Nested replace.
        var etag = await GetEtagAsync(client, "http://localhost/api/settings");
        using (
            var patch = PatchRequest(
                "http://localhost/api/settings",
                """[{"op":"replace","path":"/Database/Host","value":"db.example"}]""",
                etag
            )
        )
        using (var response = await client.SendAsync(patch))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // Array append collapses to a whole-collection change server-side.
        etag = await GetEtagAsync(client, "http://localhost/api/settings");
        using (
            var patch = PatchRequest(
                "http://localhost/api/settings",
                """[{"op":"add","path":"/Plugins/-","value":"c"}]""",
                etag
            )
        )
        using (var response = await client.SendAsync(patch))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // Move inside the array.
        etag = await GetEtagAsync(client, "http://localhost/api/settings");
        using (
            var patch = PatchRequest(
                "http://localhost/api/settings",
                """[{"op":"move","from":"/Plugins/0","path":"/Plugins/1"}]""",
                etag
            )
        )
        using (var response = await client.SendAsync(patch))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // Copy across members.
        etag = await GetEtagAsync(client, "http://localhost/api/settings");
        using (
            var patch = PatchRequest(
                "http://localhost/api/settings",
                """[{"op":"copy","from":"/Database/Host","path":"/Label"}]""",
                etag
            )
        )
        using (var response = await client.SendAsync(patch))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // Add a scalar member.
        etag = await GetEtagAsync(client, "http://localhost/api/settings");
        using (
            var patch = PatchRequest(
                "http://localhost/api/settings",
                """[{"op":"test","path":"/RetryCount","value":1},{"op":"replace","path":"/Enabled","value":false}]""",
                etag
            )
        )
        using (var response = await client.SendAsync(patch))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var text = Encoding.UTF8.GetString(await GetBodyAsync(client));
        text.ShouldContain("\"Host\":\"db.example\"");
        text.ShouldContain("\"Plugins\":[\"b\",\"a\",\"c\"]");
        text.ShouldContain("\"Label\":\"db.example\"");
        text.ShouldContain("\"Enabled\":false");
    }

    [Test]
    public async Task UnmappedProperty_Returns409WithoutWriting()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        await using var app = await StartSingleStoreAppAsync(store, "/api/settings");
        using var client = app.GetTestClient();
        var etag = await GetEtagAsync(client, "http://localhost/api/settings");

        using var patch = PatchRequest(
            "http://localhost/api/settings",
            """[{"op":"add","path":"/NoSuchProp","value":1}]""",
            etag
        );
        using var response = await client.SendAsync(patch);
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        DeserializeFragment(await GetBodyAsync(client)).RetryCount.Value.ShouldBe(1);
    }

    [Test]
    public async Task EmptyPatch_IsNoopWithSameEtag()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        await using var app = await StartSingleStoreAppAsync(store, "/api/settings");
        using var client = app.GetTestClient();
        var etag = await GetEtagAsync(client, "http://localhost/api/settings");

        using var patch = PatchRequest("http://localhost/api/settings", "[]", etag);
        using var response = await client.SendAsync(patch);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.ETag!.ToString().ShouldBe(etag);
    }

    [Test]
    public async Task PatchValidationFailure_Returns422WithoutWriting()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        await using var app = await StartSingleStoreAppAsync(store, "/api/settings");
        using var client = app.GetTestClient();
        var etag = await GetEtagAsync(client, "http://localhost/api/settings");

        using var patch = PatchRequest(
            "http://localhost/api/settings",
            """[{"op":"replace","path":"/RetryCount","value":999}]""",
            etag
        );
        using var response = await client.SendAsync(patch);
        response.StatusCode.ShouldBe((HttpStatusCode)422);

        DeserializeFragment(await GetBodyAsync(client)).RetryCount.Value.ShouldBe(1);
    }

    [Test]
    public async Task NonAtomicMultiResource_Returns409WithZeroWrites()
    {
        var first = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        var second = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { Label = Optional<string?>.Present("base") }
        );
        await using var app = await StartPatchAppAsync(
            "/api/settings",
            model =>
            {
                model.Sources(sources =>
                {
                    sources.Add(
                        new StateSource<AppSettings.Fragment>(
                            "first",
                            first,
                            new StateSourceOptions<AppSettings.Fragment>
                            {
                                Priority = 10,
                                Writer = first,
                                Watcher = first,
                            }
                        )
                    );
                    sources.Add(
                        new StateSource<AppSettings.Fragment>(
                            "second",
                            second,
                            new StateSourceOptions<AppSettings.Fragment>
                            {
                                Writer = second,
                                Watcher = second,
                            }
                        )
                    );
                });
                model.Writes(write =>
                {
                    write.DefaultTo(SourceKey<AppSettings>.Named("second"));
                    write.Route(
                        settings => settings.RetryCount,
                        SourceKey<AppSettings>.Named("first")
                    );
                });
            }
        );
        using var client = app.GetTestClient();

        var before = Encoding.UTF8.GetString(await GetBodyAsync(client));
        before.ShouldContain("\"RetryCount\":1");
        before.ShouldContain("\"Label\":\"base\"");
        var etag = await GetEtagAsync(client, "http://localhost/api/settings");

        using var patch = PatchRequest(
            "http://localhost/api/settings",
            """[{"op":"replace","path":"/RetryCount","value":2},{"op":"replace","path":"/Label","value":"moved"}]""",
            etag
        );
        using var response = await client.SendAsync(patch);
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var problem = await response.Content.ReadAsStringAsync();
        problem.ShouldContain("non-transactional");

        // The preflight rejected before the first physical write: both sources are intact.
        (await first.ReadAsync(ConfiglueResourceContext.Default)).Value!.RetryCount.Value.ShouldBe(
            1
        );
        (await second.ReadAsync(ConfiglueResourceContext.Default)).Value!.Label.Value.ShouldBe(
            "base"
        );
        Encoding.UTF8.GetString(await GetBodyAsync(client)).ShouldBe(before);
    }

    [Test]
    public async Task SharedPhysicalResource_SucceedsWithOnePhysicalWrite()
    {
        var physical = new InMemoryResource();
        var codec = new JsonStateCodec<AppSettings.Fragment>();
        await using var app = await StartPatchAppAsync(
            "/api/settings",
            model =>
            {
                model.Sources(sources =>
                {
                    IResourceReader firstSection = new JsonSectionResource(physical, "App:First");
                    sources.Add(
                        new StateSource<AppSettings.Fragment>(
                            "first",
                            new SerializedSource<AppSettings.Fragment>(
                                firstSection,
                                codec,
                                writer: firstSection as IResourceWriter,
                                watcher: firstSection as ISourceWatcher
                            ),
                            new StateSourceOptions<AppSettings.Fragment> { Priority = 10 }
                        )
                    );
                    IResourceReader secondSection = new JsonSectionResource(physical, "App:Second");
                    sources.Add(
                        new StateSource<AppSettings.Fragment>(
                            "second",
                            new SerializedSource<AppSettings.Fragment>(
                                secondSection,
                                codec,
                                writer: secondSection as IResourceWriter,
                                watcher: secondSection as ISourceWatcher
                            ),
                            new StateSourceOptions<AppSettings.Fragment>()
                        )
                    );
                });
                model.Writes(write =>
                {
                    write.DefaultTo(SourceKey<AppSettings>.Named("second"));
                    write.Route(
                        settings => settings.RetryCount,
                        SourceKey<AppSettings>.Named("first")
                    );
                });
            }
        );
        using var client = app.GetTestClient();
        var etag = await GetEtagAsync(client, "http://localhost/api/settings");

        // Two logical source writes sharing one physical resource batch atomically.
        using var patch = PatchRequest(
            "http://localhost/api/settings",
            """[{"op":"replace","path":"/RetryCount","value":9},{"op":"replace","path":"/Label","value":"shared"}]""",
            etag
        );
        using var response = await client.SendAsync(patch);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        physical.WriteCount.ShouldBe(1);

        var text = Encoding.UTF8.GetString(await GetBodyAsync(client));
        text.ShouldContain("\"RetryCount\":9");
        text.ShouldContain("\"Label\":\"shared\"");
    }

    [Test]
    public async Task ConcurrentPatches_SecondGets412AndRetrySucceeds()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            AppSettings.Fragment.From(new AppSettings { RetryCount = 1, Label = "start" })
        );
        await using var app = await StartSingleStoreAppAsync(store, "/api/settings");
        using var client = app.GetTestClient();
        var etag = await GetEtagAsync(client, "http://localhost/api/settings");

        using (
            var first = PatchRequest(
                "http://localhost/api/settings",
                """[{"op":"replace","path":"/RetryCount","value":10}]""",
                etag
            )
        )
        using (var response = await client.SendAsync(first))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using (
            var stale = PatchRequest(
                "http://localhost/api/settings",
                """[{"op":"replace","path":"/Label","value":"second"}]""",
                etag
            )
        )
        using (var response = await client.SendAsync(stale))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        }

        var fresh = await GetEtagAsync(client, "http://localhost/api/settings");
        using (
            var retry = PatchRequest(
                "http://localhost/api/settings",
                """[{"op":"replace","path":"/Label","value":"second"}]""",
                fresh
            )
        )
        using (var response = await client.SendAsync(retry))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var text = Encoding.UTF8.GetString(await GetBodyAsync(client));
        text.ShouldContain("\"RetryCount\":10");
        text.ShouldContain("\"Label\":\"second\"");
    }

    [Test]
    public async Task MapPatchDisabled_PatchReturns405()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        await using var app = await StartPatchAppAsync(
            "/api/settings",
            model =>
            {
                model.Sources(sources =>
                    sources.Add(
                        new StateSource<AppSettings.Fragment>(
                            "server-store",
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
                    write.DefaultTo(SourceKey<AppSettings>.Named("server-store"))
                );
            },
            new ConfiglueStateEndpointOptions { MapPatch = false }
        );
        using var client = app.GetTestClient();
        var etag = await GetEtagAsync(client, "http://localhost/api/settings");

        using var patch = PatchRequest(
            "http://localhost/api/settings",
            """[{"op":"replace","path":"/RetryCount","value":2}]""",
            etag
        );
        using var response = await client.SendAsync(patch);
        response.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
    }

    [Test]
    public async Task SseInvalidation_AfterPatch()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        await using var app = await StartSingleStoreAppAsync(store, "/api/settings");
        using var watcherServerClient = app.GetTestClient();
        watcherServerClient.BaseAddress = new Uri("http://localhost");
        var sseReady = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var watcherClient = new HttpClient(
            new SseReadinessHandler(watcherServerClient, sseReady)
        );
        watcherClient.BaseAddress = new Uri("http://localhost");
        using var writerClient = app.GetTestClient();

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

        var watcher = (IConfiglueRuntimeState<AppSettings>)watcherContext.GetState<AppSettings>();
        (await watcher.GetValueAsync()).RetryCount.ShouldBe(1);
        using var changeObserved = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var waitTask = watcher.OnChangeObservedAsync(changeObserved.Token);
        await sseReady.Task.WaitAsync(changeObserved.Token);

        var etag = await GetEtagAsync(writerClient, "http://localhost/api/settings");
        using (
            var patch = PatchRequest(
                "http://localhost/api/settings",
                """[{"op":"replace","path":"/RetryCount","value":71}]""",
                etag
            )
        )
        using (var response = await writerClient.SendAsync(patch))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await waitTask;
        (await watcher.GetValueAsync()).RetryCount.ShouldBe(71);
    }

    [Test]
    public async Task ClientPatchAsync_Roundtrip()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        await using var app = await StartSingleStoreAppAsync(store, "/api/settings");
        using var httpClient = app.GetTestClient();
        httpClient.BaseAddress = new Uri("http://localhost");

        var reader = new HttpStateReader<AppSettings.Fragment>(
            httpClient,
            new Uri("http://localhost/api/settings"),
            new Uri("http://localhost/api/settings/events"),
            writable: true
        );
        var baseline = await reader.ReadAsync(ConfiglueResourceContext.Default);
        baseline.Status.ShouldBe(StateReadStatus.Success);

        var document = ConfiglueJsonPatch.Parse(
            """[{"op":"replace","path":"/RetryCount","value":55}]"""
        );
        var result = await reader.PatchAsync(document, baseline.Revision!);
        result.Value!.RetryCount.Value.ShouldBe(55);
        result.Revision.ShouldNotBe(baseline.Revision);

        DeserializeFragment(await GetBodyAsync(httpClient)).RetryCount.Value.ShouldBe(55);

        // A stale baseline surfaces as a stale-ETag conflict.
        await Should.ThrowAsync<HttpStateStaleException>(async () =>
            await reader.PatchAsync(document, baseline.Revision!)
        );
    }

    [Test]
    public async Task WriterUsesPatchWhenBaselineCached_AndPutFallbackOtherwise()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        await using var app = await StartSingleStoreAppAsync(store, "/api/settings");
        var server = app.GetTestServer();
        var spy = new RecordingHandler(server.CreateHandler());
        using var httpClient = new HttpClient(spy) { BaseAddress = new Uri("http://localhost") };

        var reader = new HttpStateReader<AppSettings.Fragment>(
            httpClient,
            new Uri("http://localhost/api/settings"),
            new Uri("http://localhost/api/settings/events"),
            writable: true
        );

        // No baseline cached: the writer falls back to PUT without forcing a GET.
        var putOnly = new HttpStateReader<AppSettings.Fragment>(
            httpClient,
            new Uri("http://localhost/api/settings"),
            new Uri("http://localhost/api/settings/events"),
            writable: true
        );
        var putRevision = await GetRevisionAsync(httpClient);
        await putOnly.WriteAsync(
            ConfiglueResourceContext.Default,
            new StateWriteRequest<AppSettings.Fragment>(
                new AppSettings.Fragment { RetryCount = Optional<int>.Present(2) },
                RevisionCondition.Match(putRevision)
            )
        );
        spy.Methods.ShouldContain("PUT");
        spy.Methods.ShouldNotContain("PATCH");

        // After a read the baseline is cached: the writer derives and sends a PATCH.
        spy.Methods.Clear();
        var baseline = await reader.ReadAsync(ConfiglueResourceContext.Default);
        baseline.Status.ShouldBe(StateReadStatus.Success);
        await reader.WriteAsync(
            ConfiglueResourceContext.Default,
            new StateWriteRequest<AppSettings.Fragment>(
                new AppSettings.Fragment { RetryCount = Optional<int>.Present(3) },
                RevisionCondition.Match(baseline.Revision!)
            )
        );
        spy.Methods.ShouldContain("PATCH");
        DeserializeFragment(await GetBodyAsync(httpClient)).RetryCount.Value.ShouldBe(3);
    }

    [Test]
    public async Task WriterFallsBackToPutWhenServerDisablesPatch()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        await using var app = await StartPatchAppAsync(
            "/api/settings",
            model =>
            {
                model.Sources(sources =>
                    sources.Add(
                        new StateSource<AppSettings.Fragment>(
                            "server-store",
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
                    write.DefaultTo(SourceKey<AppSettings>.Named("server-store"))
                );
            },
            new ConfiglueStateEndpointOptions { MapPatch = false }
        );
        var server = app.GetTestServer();
        var spy = new RecordingHandler(server.CreateHandler());
        using var httpClient = new HttpClient(spy) { BaseAddress = new Uri("http://localhost") };

        var reader = new HttpStateReader<AppSettings.Fragment>(
            httpClient,
            new Uri("http://localhost/api/settings"),
            new Uri("http://localhost/api/settings/events"),
            writable: true
        );
        var baseline = await reader.ReadAsync(ConfiglueResourceContext.Default);
        await reader.WriteAsync(
            ConfiglueResourceContext.Default,
            new StateWriteRequest<AppSettings.Fragment>(
                new AppSettings.Fragment { RetryCount = Optional<int>.Present(8) },
                RevisionCondition.Match(baseline.Revision!)
            )
        );

        // PATCH was attempted first, then the writer fell back to PUT transparently.
        spy.Methods.ShouldContain("PATCH");
        spy.Methods.ShouldContain("PUT");
        DeserializeFragment(await GetBodyAsync(httpClient)).RetryCount.Value.ShouldBe(8);
    }

    [Test]
    public async Task PerSubjectPatch_IsolatesStateAndPreview()
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
        await using var app = await StartPerSubjectPatchAppAsync(perSubject, "/api/settings");
        using var client = app.GetTestClient();

        string aliceEtag;
        using (
            var aliceGet = new HttpRequestMessage(HttpMethod.Get, "http://localhost/api/settings")
        )
        {
            aliceGet.Headers.Add("X-User", "alice");
            using var response = await client.SendAsync(aliceGet);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            aliceEtag = response.Headers.ETag!.ToString();
        }

        using (
            var patch = PatchRequest(
                "http://localhost/api/settings",
                """[{"op":"replace","path":"/Label","value":"alice-patched"}]""",
                aliceEtag
            )
        )
        {
            patch.Headers.Add("X-User", "alice");
            using var response = await client.SendAsync(patch);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using (
            var aliceGet = new HttpRequestMessage(HttpMethod.Get, "http://localhost/api/settings")
        )
        {
            aliceGet.Headers.Add("X-User", "alice");
            using var response = await client.SendAsync(aliceGet);
            DeserializeFragment(await response.Content.ReadAsByteArrayAsync())
                .Label.Value.ShouldBe("alice-patched");
        }

        // Bob's subject state is untouched by Alice's patch.
        using (var bobGet = new HttpRequestMessage(HttpMethod.Get, "http://localhost/api/settings"))
        {
            bobGet.Headers.Add("X-User", "bob");
            using var response = await client.SendAsync(bobGet);
            DeserializeFragment(await response.Content.ReadAsByteArrayAsync())
                .Label.Value.ShouldBe("bob-state");
        }
    }

    private static HttpRequestMessage PatchRequest(string url, string patchJson, string etag)
    {
        var request = new HttpRequestMessage(new HttpMethod("PATCH"), url)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(patchJson)),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(
            "application/json-patch+json"
        );
        if (!string.Equals(etag, "*", StringComparison.Ordinal))
        {
            request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
        }
        else
        {
            request.Headers.TryAddWithoutValidation("If-Match", "*");
        }

        return request;
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

    private static async Task<byte[]> GetBodyAsync(HttpClient client)
    {
        using var response = await client.GetAsync("http://localhost/api/settings");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadAsByteArrayAsync();
    }

    private static async Task<string> GetEtagAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return response.Headers.ETag!.ToString();
    }

    private static async Task<string> GetRevisionAsync(HttpClient client)
    {
        var etag = await GetEtagAsync(client, "http://localhost/api/settings");
        return etag.Trim().Trim('"').ToLowerInvariant();
    }

    private static Task<WebApplication> StartSingleStoreAppAsync(
        InMemoryStateSource<AppSettings.Fragment> store,
        string pattern
    )
    {
        return StartPatchAppAsync(
            pattern,
            model =>
            {
                model.Sources(sources =>
                    sources.Add(
                        new StateSource<AppSettings.Fragment>(
                            "server-store",
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
                    write.DefaultTo(SourceKey<AppSettings>.Named("server-store"))
                );
            }
        );
    }

    private static async Task<WebApplication> StartPatchAppAsync(
        string pattern,
        Action<ConfiglueModelBuilder<AppSettings>> configure,
        ConfiglueStateEndpointOptions? options = null
    )
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddConfiglue(config => config.Add<AppSettings>(configure));
        var app = builder.Build();
        app.MapConfiglueState<AppSettings>(pattern, options);
        await app.StartAsync();
        return app;
    }

    private sealed class RecordingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public List<string> Methods { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            lock (Methods)
            {
                Methods.Add(request.Method.Method);
            }

            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class SseReadinessHandler(
        HttpClient inner,
        TaskCompletionSource<bool> readiness
    ) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var isEvents =
                request.RequestUri?.AbsolutePath.EndsWith(
                    "/events",
                    StringComparison.OrdinalIgnoreCase
                ) == true;
            var clone = new HttpRequestMessage(request.Method, request.RequestUri);
            if (request.Content is not null)
            {
                clone.Content = request.Content;
            }

            foreach (var header in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            var response = await inner
                .SendAsync(clone, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (isEvents && response.IsSuccessStatusCode)
            {
                readiness.TrySetResult(true);
            }

            return response;
        }
    }

    private static async Task<WebApplication> StartPerSubjectPatchAppAsync(
        KeyedMemorySource store,
        string pattern
    )
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
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
}
