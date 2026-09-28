using System.Buffers;
using System.CommandLine;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Configlue.Extensions.MSOptions;
using Configlue.Provider.Json;
using Configlue.Provider.Xml;
using Configlue.Provider.Yaml;
using Configlue.Resource.Http;
using Configlue.Source.CommandLine;
using Configlue.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Configlue.Tests;

public sealed partial class ConfiglueFacadeSourceTests
{
    [Test]
    public async Task FacadeCreationFailureDisposesEarlierHelperCreatedFileResources()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "first.json");

        Should.Throw<InvalidOperationException>(() =>
            ConfiglueApp.CreateContext(builder =>
            {
                builder.Add<AppSettings>(model =>
                    model.Sources(sources =>
                    {
                        sources.FromJsonFile(
                            new JsonFileSourceOptions
                            {
                                Id = "first-file",
                                Path = path,
                                WatchChanges = true,
                                ResourceOptions = new FileResourceOptions { CreateBackup = false },
                            }
                        );
                        sources.Add<AppSettings.Fragment>(_ =>
                            throw new InvalidOperationException("factory failed")
                        );
                    })
                );
            })
        );

        await File.WriteAllTextAsync(path, "{}");
        File.Delete(path);
        (Directory.Exists(directory.FullPath)).ShouldBeTrue();
    }

    [Test]
    public async Task ContextShutdownStopsAndDisposesHelperCreatedFileWatcherOnce()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "watched.json");
        var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                    sources.FromJsonFile(
                        new JsonFileSourceOptions
                        {
                            Id = "watched-json",
                            Path = path,
                            WatchChanges = true,
                            ReadOnly = true,
                        }
                    )
                )
            );
        });
        var resource = context.OwnedResourcesForTests.OfType<FileResource>().Single();
        using var subscription = context.GetOptions<AppSettings>().OnChange(static _ => { });

        await WaitUntilAsync(() => resource.HasActiveWatcherForTests);
        var firstDisposal = context.DisposeAsync().AsTask();
        var secondDisposal = context.DisposeAsync().AsTask();
        await Task.WhenAll(firstDisposal, secondDisposal);

        firstDisposal.ShouldBeSameAs(secondDisposal);
        (resource.IsDisposedForTests).ShouldBeTrue();
        (resource.HasActiveWatcherForTests).ShouldBeFalse();
        (resource.DisposeCallCountForTests).ShouldBe(1);
    }

    [Test]
    public async Task ContextShutdownLeavesCallerSuppliedFileResourceOwnedByCaller()
    {
        using var directory = new TemporaryDirectory();
        var resource = new FileResource(Path.Combine(directory.FullPath, "caller.json"));
        var source = SerializedStateSource.FromResource<AppSettings.Fragment>(
            "caller-json",
            resource,
            new JsonStateCodec<AppSettings.Fragment>()
        );
        var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model => model.Sources(sources => sources.Add(source)));
        });
        using var subscription = context.GetOptions<AppSettings>().OnChange(static _ => { });

        await WaitUntilAsync(() => resource.HasActiveWatcherForTests);
        await context.DisposeAsync();

        (resource.IsDisposedForTests).ShouldBeFalse();
        (resource.HasActiveWatcherForTests).ShouldBeTrue();
        (resource.DisposeCallCountForTests).ShouldBe(0);

        resource.Dispose();
        (resource.IsDisposedForTests).ShouldBeTrue();
    }

    [Test]
    public async Task RemovingDynamicOptionsStopsAndDisposesItsHelperCreatedFileWatcherOnce()
    {
        using var directory = new TemporaryDirectory();
        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.EnableDynamicOptions = true;
                model.ConfigureSources(registration =>
                    registration.Sources.FromJsonFile(
                        new JsonFileSourceOptions
                        {
                            Id = $"dynamic-{registration.OptionsName}",
                            Path = Path.Combine(
                                directory.FullPath,
                                $"{registration.OptionsName}.json"
                            ),
                            ReadOnly = true,
                            WatchChanges = true,
                        }
                    )
                );
            });
        });
        var registry = context.GetOptionsRegistry<AppSettings>();
        registry.TryAdd("late").ShouldBeTrue();
        var resource = ((ConfiglueFacadeOptionsRegistry<AppSettings>)registry)
            .GetOwnedResourcesForTests("late")
            .OfType<FileResource>()
            .Single();
        var handle = registry.Get("late");
        using var subscription = handle.OnChange(static _ => { });

        await WaitUntilAsync(() => resource.HasActiveWatcherForTests);
        (await registry.TryRemoveAsync("late")).ShouldBeTrue();

        (resource.IsDisposedForTests).ShouldBeTrue();
        (resource.HasActiveWatcherForTests).ShouldBeFalse();
        (resource.DisposeCallCountForTests).ShouldBe(1);
        await Should.ThrowAsync<ObjectDisposedException>(async () => await handle.GetValueAsync());
    }

    [Test]
    public async Task RemovingAndDisposingFacadeProfilesStopsAndDisposesTheirFileWatchersOnce()
    {
        using var directory = new TemporaryDirectory();
        var catalogStore = new InMemoryStateStore<ConfiglueProfileCatalog>();
        var catalog = new StateSource<ConfiglueProfileCatalog>(
            "catalog",
            catalogStore,
            writer: catalogStore,
            watcher: catalogStore
        );
        var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.EnableProfiles(catalog);
                model.ConfigureSources(registration =>
                    registration.Sources.FromJsonFile(
                        new JsonFileSourceOptions
                        {
                            Id = $"profile-{registration.OptionsName}",
                            Path = Path.Combine(
                                directory.FullPath,
                                $"{registration.OptionsName}.json"
                            ),
                            WatchChanges = true,
                        }
                    )
                );
            });
        });
        var profiles = context.GetProfiledOptions<AppSettings>();
        await profiles.GetProfileNamesAsync();
        await profiles.CreateProfileAsync("removed", copyFrom: "default");
        await profiles.CreateProfileAsync("context-end", copyFrom: "default");

        var registry =
            (ConfiglueFacadeOptionsRegistry<AppSettings>)context.GetOptionsRegistry<AppSettings>();
        var removedResource = registry
            .GetOwnedResourcesForTests("removed")
            .OfType<FileResource>()
            .Single();
        var contextEndResource = registry
            .GetOwnedResourcesForTests("context-end")
            .OfType<FileResource>()
            .Single();
        using var removedSubscription = context
            .GetOptions<AppSettings>("removed")
            .OnChange(static _ => { });
        using var contextEndSubscription = context
            .GetOptions<AppSettings>("context-end")
            .OnChange(static _ => { });

        await WaitUntilAsync(() =>
            removedResource.HasActiveWatcherForTests && contextEndResource.HasActiveWatcherForTests
        );
        await profiles.RemoveProfileAsync("removed");

        (removedResource.IsDisposedForTests).ShouldBeTrue();
        (removedResource.HasActiveWatcherForTests).ShouldBeFalse();
        (removedResource.DisposeCallCountForTests).ShouldBe(1);

        await context.DisposeAsync();

        (contextEndResource.IsDisposedForTests).ShouldBeTrue();
        (contextEndResource.HasActiveWatcherForTests).ShouldBeFalse();
        (contextEndResource.DisposeCallCountForTests).ShouldBe(1);
    }

    [Test]
    public async Task DynamicSourceFactoryFailureDisposesResourcesCreatedEarlierInItsRuntime()
    {
        var resource = new DisposableProbe();
        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.EnableDynamicOptions = true;
                model.ConfigureSources(registration =>
                {
                    if (registration.OptionsName != "failure")
                    {
                        var store = new InMemoryStateStore<AppSettings.Fragment>();
                        registration.Sources.Add<AppSettings.Fragment>(
                            _ => new StateSource<AppSettings.Fragment>(
                                "ordinary",
                                store,
                                writer: store,
                                watcher: store
                            )
                        );
                        return;
                    }

                    registration.Sources.Add(new DisposableProbeSourceDefinition(resource));
                    registration.Sources.Add<AppSettings.Fragment>(_ =>
                        throw new InvalidOperationException("source factory failed")
                    );
                });
            });
        });
        var registry = context.GetOptionsRegistry<AppSettings>();

        Should.Throw<InvalidOperationException>(() => registry.TryAdd("failure"));

        (resource.DisposeCallCount).ShouldBe(1);
        registry.TryGet("failure", out _).ShouldBeFalse();
    }

    private static async Task<ResourceId?> WriteHttpPatchAndGetResourceIdAsync(
        string endpoint,
        ResourceId? resourceId
    )
    {
        using var client = new HttpClient(new NoContentHttpHandler());
        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                    sources.FromHttp(
                        new HttpSourceOptions
                        {
                            Id = "http-settings",
                            EndPoint = endpoint,
                            Client = client,
                            Codec = new JsonStateCodec<AppSettings.Fragment>(),
                            Writable = true,
                            WatchChanges = false,
                            ResourceId = resourceId,
                        }
                    )
                )
            );
        });

        var result = await (
            (IConfiglueRuntimeOptions<AppSettings>)context.GetOptions<AppSettings>()
        ).ApplyPatchesAsync([
            new StateSourcePatch(
                "http-settings",
                new AppSettings.Patch { RetryCount = FragmentOperation<int>.Set(2) }
            ),
        ]);
        (result.PhysicalWriteCount).ShouldBe(1);
        return result.Sources.Single().ResourceId;
    }

    private static async Task WriteFragmentAsync(string path, AppSettings.Fragment fragment)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var output = new ArrayBufferWriter<byte>();
        var codec = new JsonStateCodec<AppSettings.Fragment>();
        codec.Serialize(fragment, output, default);
        await File.WriteAllBytesAsync(path, output.WrittenMemory.ToArray());
    }

    private static byte[] SerializeFragment(AppSettings.Fragment fragment)
    {
        var output = new ArrayBufferWriter<byte>();
        new JsonStateCodec<AppSettings.Fragment>().Serialize(fragment, output, default);
        return output.WrittenSpan.ToArray();
    }

    private static JsonNode? FindJsonProperty(JsonNode node, string propertyName)
    {
        if (node is JsonObject obj)
        {
            if (obj.TryGetPropertyValue(propertyName, out var value))
            {
                return value;
            }
            foreach (var child in obj.Select(static pair => pair.Value))
            {
                if (child is not null && FindJsonProperty(child, propertyName) is { } found)
                {
                    return found;
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                if (child is not null && FindJsonProperty(child, propertyName) is { } found)
                {
                    return found;
                }
            }
        }
        return null;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            FullPath = Path.Combine(
                Path.GetTempPath(),
                "Configlue.Tests",
                Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(FullPath);
        }

        public string FullPath { get; }

        public void Dispose()
        {
            if (Directory.Exists(FullPath))
            {
                Directory.Delete(FullPath, recursive: true);
            }
        }
    }

    private sealed class DisposableProbe : IDisposable
    {
        public int DisposeCallCount { get; private set; }

        public void Dispose() => DisposeCallCount++;
    }

    private sealed class DisposableProbeSourceDefinition(DisposableProbe resource)
        : IConfiglueSourceDefinition
    {
        public ConfiglueSourceCreation<TFragment> Create<TFragment>(
            ConfiglueSourceCreationContext context
        )
            where TFragment : class, IConfiglueFragment<TFragment>
        {
            return context.Complete(CreateSourceCore<TFragment>(context.Own));
        }

        private StateSource<TFragment> CreateSourceCore<TFragment>(Action<IDisposable> ownResource)
            where TFragment : class, IConfiglueFragment<TFragment>
        {
            ownResource(resource);
            var store = new InMemoryStateStore<TFragment>();
            return new StateSource<TFragment>("owned-probe", store, writer: store, watcher: store);
        }
    }

    private sealed class NoContentHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                request.Method == HttpMethod.Get
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.NoContent)
            );
    }

    private sealed class RecordingHttpHandler(byte[] content) : HttpMessageHandler
    {
        public List<HttpMethod> RequestMethods { get; } = [];

        public bool IsDisposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            RequestMethods.Add(request.Method);
            return Task.FromResult(
                request.Method == HttpMethod.Get
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(content),
                    }
                    : new HttpResponseMessage(HttpStatusCode.NoContent)
            );
        }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class HttpResponseHandler(
        System.Collections.Concurrent.ConcurrentBag<Uri> requestedUris,
        byte[] content
    ) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            if (request.RequestUri is { } requestUri)
            {
                requestedUris.Add(requestUri);
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content),
            };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\"");
            return Task.FromResult(response);
        }
    }
}
