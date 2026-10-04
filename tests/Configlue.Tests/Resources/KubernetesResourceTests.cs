using System.Runtime.CompilerServices;
using System.Text;
using Configlue.Provider.Json;
using Configlue.Resource.Kubernetes;

namespace Configlue.Tests;

public sealed class KubernetesResourceTests
{
    [Test]
    public async Task ConfigMapKeyRead_ReturnsContentAndResourceVersion()
    {
        var client = new FakeKubernetesObjectClient();
        client.SeedConfigMap(
            "app-config",
            "settings",
            "100",
            new Dictionary<string, string> { ["appsettings.json"] = """{"RetryCount":5}""" },
            new Dictionary<string, byte[]> { ["logo"] = [1, 2, 3] }
        );
        using var resource = new KubernetesResource(
            client,
            KubernetesResourceKind.ConfigMap,
            "app-config",
            "settings",
            "appsettings.json"
        );

        var text = await resource.ReadAsync(CreateContext());
        text.Status.ShouldBe(StateReadStatus.Success);
        text.Revision.ShouldBe("100");
        Encoding.UTF8.GetString(text.Content.Span).ShouldBe("""{"RetryCount":5}""");

        using var binaryResource = new KubernetesResource(
            client,
            KubernetesResourceKind.ConfigMap,
            "app-config",
            "settings",
            "logo"
        );
        var binary = await binaryResource.ReadAsync(CreateContext());
        binary.Content.ToArray().ShouldBe(new byte[] { 1, 2, 3 });
        binary.Revision.ShouldBe("100");
    }

    [Test]
    public async Task SecretKeyRead_ReturnsBinaryAndRevision()
    {
        var client = new FakeKubernetesObjectClient();
        client.SeedSecret(
            "app-config",
            "credentials",
            "7",
            new Dictionary<string, byte[]> { ["password"] = Encoding.UTF8.GetBytes("s3cr3t") }
        );
        using var resource = new KubernetesResource(
            client,
            KubernetesResourceKind.Secret,
            "app-config",
            "credentials",
            "password"
        );

        var result = await resource.ReadAsync(CreateContext());
        result.Status.ShouldBe(StateReadStatus.Success);
        result.Revision.ShouldBe("7");
        Encoding.UTF8.GetString(result.Content.Span).ShouldBe("s3cr3t");
    }

    [Test]
    public async Task WholeObjectRead_IsDeterministicJson()
    {
        var client = new FakeKubernetesObjectClient();
        client.SeedConfigMap(
            "app-config",
            "settings",
            "3",
            new Dictionary<string, string> { ["b"] = "2", ["a"] = "1" },
            new Dictionary<string, byte[]> { ["z"] = [9] }
        );
        using var resource = new KubernetesResource(
            client,
            KubernetesResourceKind.ConfigMap,
            "app-config",
            "settings",
            null
        );

        var first = await resource.ReadAsync(CreateContext());
        var second = await resource.ReadAsync(CreateContext());
        Encoding
            .UTF8.GetString(first.Content.Span)
            .ShouldBe(Encoding.UTF8.GetString(second.Content.Span));
        Encoding
            .UTF8.GetString(first.Content.Span)
            .ShouldBe("""{"data":{"a":"1","b":"2"},"binaryData":{"z":"CQ=="}}""");

        var expected = await ExpectedReadAsync(first.Content.ToArray(), first.Revision);
        expected.Status.ShouldBe(StateReadStatus.Success);
        expected.Revision.ShouldBe("3");
    }

    [Test]
    public async Task MissingObject_MapsToNotFound()
    {
        var client = new FakeKubernetesObjectClient();
        using var configMap = new KubernetesResource(
            client,
            KubernetesResourceKind.ConfigMap,
            "app-config",
            "missing",
            "key"
        );
        using var secret = new KubernetesResource(
            client,
            KubernetesResourceKind.Secret,
            "app-config",
            "missing",
            "key"
        );

        (await configMap.ReadAsync(CreateContext())).Status.ShouldBe(StateReadStatus.NotFound);
        (await secret.ReadAsync(CreateContext())).Status.ShouldBe(StateReadStatus.NotFound);
    }

    [Test]
    public async Task MissingKey_MapsToNotFoundWithRevision()
    {
        var client = new FakeKubernetesObjectClient();
        client.SeedConfigMap(
            "app-config",
            "settings",
            "42",
            new Dictionary<string, string> { ["present"] = "yes" },
            new Dictionary<string, byte[]>()
        );
        using var resource = new KubernetesResource(
            client,
            KubernetesResourceKind.ConfigMap,
            "app-config",
            "settings",
            "absent"
        );

        var result = await resource.ReadAsync(CreateContext());
        result.Status.ShouldBe(StateReadStatus.NotFound);
        result.Revision.ShouldBe("42");
    }

    [Test]
    public async Task SingleKeyWrite_PreservesUnrelatedKeys()
    {
        var client = new FakeKubernetesObjectClient();
        client.SeedConfigMap(
            "app-config",
            "settings",
            "1",
            new Dictionary<string, string> { ["keep"] = "a", ["target"] = "old" },
            new Dictionary<string, byte[]>()
        );
        client.SeedSecret(
            "app-config",
            "credentials",
            "1",
            new Dictionary<string, byte[]> { ["keep"] = [7], ["target"] = [8] }
        );
        using var configMap = new KubernetesResource(
            client,
            KubernetesResourceKind.ConfigMap,
            "app-config",
            "settings",
            "target"
        );
        using var secret = new KubernetesResource(
            client,
            KubernetesResourceKind.Secret,
            "app-config",
            "credentials",
            "target"
        );

        var mapWrite = await configMap.WriteAsync(
            CreateContext(),
            new ResourceWriteRequest(
                Encoding.UTF8.GetBytes("new"),
                Condition: RevisionCondition.Match("1")
            )
        );
        mapWrite.Revision.ShouldNotBe("1");
        mapWrite.Revision.ShouldNotBeNull();

        var secretWrite = await secret.WriteAsync(
            CreateContext(),
            new ResourceWriteRequest(new byte[] { 9 }, Condition: RevisionCondition.Match("1"))
        );
        secretWrite.Revision.ShouldNotBe("1");
        secretWrite.Revision.ShouldNotBeNull();

        var mapState = client.GetConfigMapState("app-config", "settings");
        mapState.Data["keep"].ShouldBe("a");
        mapState.Data["target"].ShouldBe("new");
        var secretState = client.GetSecretState("app-config", "credentials");
        secretState.Data["keep"].ShouldBe(new byte[] { 7 });
        secretState.Data["target"].ShouldBe(new byte[] { 9 });
    }

    [Test]
    public async Task StaleWrite_FailsInsteadOfOverwriting()
    {
        var client = new FakeKubernetesObjectClient();
        client.SeedConfigMap(
            "app-config",
            "settings",
            "5",
            new Dictionary<string, string> { ["key"] = "current" },
            new Dictionary<string, byte[]>()
        );
        using var resource = new KubernetesResource(
            client,
            KubernetesResourceKind.ConfigMap,
            "app-config",
            "settings",
            "key"
        );

        await Should.ThrowAsync<KubernetesConflictException>(async () =>
            await resource.WriteAsync(
                CreateContext(),
                new ResourceWriteRequest(
                    Encoding.UTF8.GetBytes("stale"),
                    Condition: RevisionCondition.Match("4")
                )
            )
        );

        await Should.ThrowAsync<StateConflictException>(async () =>
            await resource.WriteAsync(
                CreateContext(),
                new ResourceWriteRequest(
                    Encoding.UTF8.GetBytes("x"),
                    Condition: RevisionCondition.MustNotExist
                )
            )
        );

        var current = await resource.ReadAsync(CreateContext());
        Encoding.UTF8.GetString(current.Content.Span).ShouldBe("current");
    }

    [Test]
    public async Task ImmutableObject_RejectsWrites()
    {
        var client = new FakeKubernetesObjectClient();
        client.SeedConfigMap(
            "app-config",
            "locked",
            "9",
            new Dictionary<string, string> { ["key"] = "v" },
            new Dictionary<string, byte[]>(),
            immutable: true
        );
        client.SeedSecret(
            "app-config",
            "locked",
            "9",
            new Dictionary<string, byte[]> { ["key"] = [1] },
            immutable: true
        );
        using var configMap = new KubernetesResource(
            client,
            KubernetesResourceKind.ConfigMap,
            "app-config",
            "locked",
            "key"
        );
        using var secret = new KubernetesResource(
            client,
            KubernetesResourceKind.Secret,
            "app-config",
            "locked",
            "key"
        );

        (await configMap.ReadAsync(CreateContext())).Status.ShouldBe(StateReadStatus.Success);
        await Should.ThrowAsync<KubernetesImmutableException>(async () =>
            await configMap.WriteAsync(
                CreateContext(),
                new ResourceWriteRequest(Encoding.UTF8.GetBytes("x"))
            )
        );
        await Should.ThrowAsync<KubernetesImmutableException>(async () =>
            await secret.WriteAsync(CreateContext(), new ResourceWriteRequest(new byte[] { 2 }))
        );
    }

    [Test]
    public async Task WatchModifiedEvent_InvalidatesWaiter()
    {
        var client = new FakeKubernetesObjectClient();
        client.SeedConfigMap(
            "app-config",
            "settings",
            "1",
            new Dictionary<string, string> { ["key"] = "v1" },
            new Dictionary<string, byte[]>()
        );
        client.WatchHandler = (_, _, _, _) =>
            TestStreams.Of(
                new KubernetesWatchEvent(
                    KubernetesWatchType.Modified,
                    "2",
                    configMap: new KubernetesConfigMapSnapshot(
                        "2",
                        false,
                        new Dictionary<string, string> { ["key"] = "v2" },
                        new Dictionary<string, byte[]>()
                    )
                )
            );
        using var resource = new KubernetesResource(
            client,
            KubernetesResourceKind.ConfigMap,
            "app-config",
            "settings",
            "key"
        );

        await resource.WaitForChangeAsync(CreateContext(), "1");
        client.WatchCalls.ShouldBe(1);
        client.LastWatchResourceVersion.ShouldBe("1");
    }

    [Test]
    public async Task WatchDeletedEvent_InvalidatesAndReadConvergesToNotFound()
    {
        var client = new FakeKubernetesObjectClient();
        client.SeedSecret(
            "app-config",
            "credentials",
            "1",
            new Dictionary<string, byte[]> { ["password"] = [1] }
        );
        client.WatchHandler = (_, _, _, _) =>
            TestStreams.Of(new KubernetesWatchEvent(KubernetesWatchType.Deleted, "2"));
        using var resource = new KubernetesResource(
            client,
            KubernetesResourceKind.Secret,
            "app-config",
            "credentials",
            "password"
        );

        await resource.WaitForChangeAsync(CreateContext(), "1");

        client.DeleteSecret("app-config", "credentials");
        (await resource.ReadAsync(CreateContext())).Status.ShouldBe(StateReadStatus.NotFound);
    }

    [Test]
    public async Task WatchBookmark_DoesNotSignalByItself()
    {
        var client = new FakeKubernetesObjectClient();
        client.SeedConfigMap(
            "app-config",
            "settings",
            "1",
            new Dictionary<string, string> { ["key"] = "v1" },
            new Dictionary<string, byte[]>()
        );
        client.WatchHandler = (_, _, _, _) =>
            TestStreams.Of(new KubernetesWatchEvent(KubernetesWatchType.Bookmark, "2"));
        using var resource = new KubernetesResource(
            client,
            KubernetesResourceKind.ConfigMap,
            "app-config",
            "settings",
            "key",
            new KubernetesResourceOptions
            {
                WatchReconnectInitialDelay = TimeSpan.FromMilliseconds(10),
                WatchReconnectMaxDelay = TimeSpan.FromMilliseconds(20),
            }
        );

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        // A bookmark-only stream ends without an event, so the waiter reconnects and the
        // fresh GET still matches: cancellation must win instead of a false invalidation.
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await resource.WaitForChangeAsync(CreateContext(), "1", timeout.Token)
        );
    }

    [Test]
    public async Task WatchStreamTermination_ReconnectsAndConverges()
    {
        var client = new FakeKubernetesObjectClient();
        client.SeedConfigMap(
            "app-config",
            "settings",
            "1",
            new Dictionary<string, string> { ["key"] = "v1" },
            new Dictionary<string, byte[]>()
        );
        var calls = 0;
        client.WatchHandler = (_, _, _, _) =>
        {
            calls++;
            return calls == 1
                ? TestStreams.Empty<KubernetesWatchEvent>()
                : TestStreams.Of(
                    new KubernetesWatchEvent(
                        KubernetesWatchType.Modified,
                        "2",
                        configMap: new KubernetesConfigMapSnapshot(
                            "2",
                            false,
                            new Dictionary<string, string> { ["key"] = "v2" },
                            new Dictionary<string, byte[]>()
                        )
                    )
                );
        };
        using var resource = new KubernetesResource(
            client,
            KubernetesResourceKind.ConfigMap,
            "app-config",
            "settings",
            "key",
            new KubernetesResourceOptions
            {
                WatchReconnectInitialDelay = TimeSpan.FromMilliseconds(1),
                WatchReconnectMaxDelay = TimeSpan.FromMilliseconds(2),
            }
        );

        await resource.WaitForChangeAsync(CreateContext(), "1");
        calls.ShouldBe(2);
    }

    [Test]
    public async Task WatchExpiredVersion_RefreshesAndResubscribes()
    {
        var client = new FakeKubernetesObjectClient();
        client.SeedConfigMap(
            "app-config",
            "settings",
            "1",
            new Dictionary<string, string> { ["key"] = "v1" },
            new Dictionary<string, byte[]>()
        );
        var calls = 0;
        client.WatchHandler = (_, _, _, _) =>
        {
            calls++;
            return calls == 1
                ? TestStreams.Expired<KubernetesWatchEvent>()
                : TestStreams.Of(
                    new KubernetesWatchEvent(
                        KubernetesWatchType.Modified,
                        "1",
                        configMap: new KubernetesConfigMapSnapshot(
                            "1",
                            false,
                            new Dictionary<string, string> { ["key"] = "v1" },
                            new Dictionary<string, byte[]>()
                        )
                    )
                );
        };
        using var resource = new KubernetesResource(
            client,
            KubernetesResourceKind.ConfigMap,
            "app-config",
            "settings",
            "key",
            new KubernetesResourceOptions
            {
                WatchReconnectInitialDelay = TimeSpan.FromMilliseconds(1),
                WatchReconnectMaxDelay = TimeSpan.FromMilliseconds(2),
            }
        );

        // Fresh GET still reports the observed revision, so the waiter resubscribes
        // instead of treating expiry as a change.
        await resource.WaitForChangeAsync(CreateContext(), "1");
        calls.ShouldBe(2);
    }

    [Test]
    public async Task WatchExpiredVersion_ReturnsImmediatelyWhenAlreadyChanged()
    {
        var client = new FakeKubernetesObjectClient();
        client.SeedConfigMap(
            "app-config",
            "settings",
            "2",
            new Dictionary<string, string> { ["key"] = "v2" },
            new Dictionary<string, byte[]>()
        );
        var calls = 0;
        client.WatchHandler = (_, _, _, _) =>
        {
            calls++;
            return TestStreams.Expired<KubernetesWatchEvent>();
        };
        using var resource = new KubernetesResource(
            client,
            KubernetesResourceKind.ConfigMap,
            "app-config",
            "settings",
            "key"
        );

        await resource.WaitForChangeAsync(CreateContext(), "1");
        calls.ShouldBe(1);
    }

    [Test]
    public async Task WaitForChange_RespectsCancellation()
    {
        var client = new FakeKubernetesObjectClient();
        client.WatchHandler = (_, _, _, _) => TestStreams.Never<KubernetesWatchEvent>();
        using var resource = new KubernetesResource(
            client,
            KubernetesResourceKind.Secret,
            "app-config",
            "credentials",
            "password"
        );

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await resource.WaitForChangeAsync(CreateContext(), "1", cancelled.Token)
        );
    }

    [Test]
    public async Task Dispose_WakesWaitersSuccessfully()
    {
        var client = new FakeKubernetesObjectClient();
        client.WatchHandler = (_, _, _, _) => TestStreams.Never<KubernetesWatchEvent>();
        var resource = new KubernetesResource(
            client,
            KubernetesResourceKind.ConfigMap,
            "app-config",
            "settings",
            "key"
        );

        var wait = resource.WaitForChangeAsync(CreateContext(), "1").AsTask();
        await Task.Delay(50);
        resource.Dispose();
        await wait;
        resource.Dispose();
    }

    [Test]
    public async Task WholeObjectWrite_ReplacesEntireKeySet()
    {
        var client = new FakeKubernetesObjectClient();
        client.SeedConfigMap(
            "app-config",
            "settings",
            "1",
            new Dictionary<string, string> { ["retired"] = "gone", ["keep"] = "old" },
            new Dictionary<string, byte[]>()
        );
        using var resource = new KubernetesResource(
            client,
            KubernetesResourceKind.ConfigMap,
            "app-config",
            "settings",
            null
        );

        var payload = KubernetesObjectEncoding.EncodeConfigMapObject(
            new Dictionary<string, string> { ["keep"] = "new" },
            new Dictionary<string, byte[]>()
        );
        var write = await resource.WriteAsync(
            CreateContext(),
            new ResourceWriteRequest(payload, Condition: RevisionCondition.Match("1"))
        );
        write.Revision.ShouldNotBe("1");

        var state = client.GetConfigMapState("app-config", "settings");
        state.Data.Count.ShouldBe(1);
        state.Data["keep"].ShouldBe("new");

        var read = await resource.ReadAsync(CreateContext());
        read.Revision.ShouldBe(write.Revision);
    }

    [Test]
    public async Task ConfigMapWrite_RejectsInvalidUtf8()
    {
        var client = new FakeKubernetesObjectClient();
        client.SeedConfigMap(
            "app-config",
            "settings",
            "1",
            new Dictionary<string, string> { ["key"] = "v" },
            new Dictionary<string, byte[]>()
        );
        using var resource = new KubernetesResource(
            client,
            KubernetesResourceKind.ConfigMap,
            "app-config",
            "settings",
            "key"
        );

        await Should.ThrowAsync<InvalidDataException>(async () =>
            await resource.WriteAsync(
                CreateContext(),
                new ResourceWriteRequest(new byte[] { 0xFF, 0xFE })
            )
        );
    }

    [Test]
    public void InClusterConfiguration_LoadsFromEnvironment()
    {
        Environment.SetEnvironmentVariable("KUBERNETES_SERVICE_HOST", "10.0.0.1");
        Environment.SetEnvironmentVariable("KUBERNETES_SERVICE_PORT", "443");
        try
        {
            KubernetesConfiguration.IsInCluster.ShouldBeTrue();
            var configuration = KubernetesConfiguration.ForInCluster("app-config");
            configuration.Server.ShouldBe("https://10.0.0.1:443");
            configuration.DefaultNamespace.ShouldBe("app-config");
            configuration.ToString().ShouldNotContain("token");
        }
        finally
        {
            Environment.SetEnvironmentVariable("KUBERNETES_SERVICE_HOST", null);
            Environment.SetEnvironmentVariable("KUBERNETES_SERVICE_PORT", null);
        }

        KubernetesConfiguration.IsInCluster.ShouldBeFalse();
    }

    [Test]
    public void KubeConfigFile_LoadsServerNamespaceAndToken()
    {
        var ca = Convert.ToBase64String(Encoding.UTF8.GetBytes("fake-ca-pem"));
        var path = Path.Combine(Path.GetTempPath(), $"kubeconfig-{Guid.NewGuid():N}");
        File.WriteAllText(
            path,
            """
            apiVersion: v1
            kind: Config
            current-context: dev
            contexts:
            - name: dev
              context:
                cluster: dev-cluster
                user: dev-user
                namespace: app-config
            clusters:
            - name: dev-cluster
              cluster:
                server: https://192.168.1.10:6443
                certificate-authority-data: __CA__
            users:
            - name: dev-user
              user:
                token: file-token-123
            """.Replace("__CA__", ca)
        );
        try
        {
            var configuration = KubernetesConfiguration.FromKubeConfigFile(path);
            configuration.Server.ShouldBe("https://192.168.1.10:6443");
            configuration.DefaultNamespace.ShouldBe("app-config");
            configuration.Token.ShouldBe("file-token-123");
            configuration.ToString().ShouldNotContain("file-token-123");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void InjectedConfiguration_NeverLeaksCredentials()
    {
        var configuration = new KubernetesConfiguration("https://cluster:6443", "app-config")
        {
            Token = "super-secret-token",
            CaCertPem = "super-secret-ca",
        };
        using var httpClient = configuration.CreateHttpClient();
        var objectClient = KubernetesConfiguration.CreateObjectClient(httpClient);

        using var resource = new KubernetesResource(
            objectClient,
            KubernetesResourceKind.Secret,
            "app-config",
            "credentials",
            "password"
        );
        var context = CreateContext();

        configuration.ToString().ShouldNotContain("super-secret-token");
        configuration.ToString().ShouldNotContain("super-secret-ca");
        resource.ToString().ShouldNotContain("s3cr3t");
        resource.GetResourceId(context).Value.ShouldNotContain("s3cr3t");
        httpClient.DefaultRequestHeaders.Authorization?.ToString().ShouldContain("Bearer");
    }

    [Test]
    public void SecretValues_NeverLeakThroughDiagnostics()
    {
        var client = new FakeKubernetesObjectClient();
        var secretValue = Encoding.UTF8.GetBytes("top-secret-value");
        client.SeedSecret(
            "app-config",
            "credentials",
            "1",
            new Dictionary<string, byte[]> { ["password"] = secretValue }
        );
        using var resource = new KubernetesResource(
            client,
            KubernetesResourceKind.Secret,
            "app-config",
            "credentials",
            "password"
        );
        var context = CreateContext();
        var secretText = Encoding.UTF8.GetString(secretValue);

        resource.ToString().ShouldNotContain(secretText);
        resource.GetResourceId(context).Value.ShouldNotContain(secretText);
        resource.GetResourceId(context).ShouldBe(resource.GetResourceId(CreateContext()));
        try
        {
            throw new KubernetesImmutableException(
                "The Secret 'app-config/credentials' is immutable."
            );
        }
        catch (KubernetesImmutableException exception)
        {
            exception.Message.ShouldNotContain(secretText);
        }
    }

    [Test]
    public void NamespaceChanges_AddressDifferentObjects()
    {
        var client = new FakeKubernetesObjectClient();
        using var resource = new KubernetesResource(
            client,
            KubernetesResourceKind.ConfigMap,
            "default",
            "settings",
            "key",
            new KubernetesResourceOptions
            {
                NamespaceSelector = context =>
                    context.Subject.Key == SubjectKey.From("tenant-a") ? "team-a" : "team-b",
            }
        );

        var first = new ConfiglueResourceContext(
            new FakeSubject(SubjectKey.From("tenant-a")),
            ResourceKey.From("settings"),
            RouteKey.Default
        );
        var second = new ConfiglueResourceContext(
            new FakeSubject(SubjectKey.From("tenant-b")),
            ResourceKey.From("settings"),
            RouteKey.Default
        );
        resource.GetResourceId(first).ShouldNotBe(resource.GetResourceId(second));
    }

    [Test]
    public void Registration_RequiresExactlyOneClientSource()
    {
        Should.Throw<ArgumentException>(() =>
            ConfiglueApp.CreateContext(builder =>
            {
                builder.Add<AppSettings>(model =>
                    model.Sources(sources =>
                    {
                        sources.FromKubernetes(
                            new KubernetesSourceOptions
                            {
                                Kind = KubernetesResourceKind.ConfigMap,
                                Namespace = "app-config",
                                Name = "settings",
                                Key = "appsettings.json",
                                Codec = StateCodecBinding.Typed(
                                    new JsonStateCodec<AppSettings.Fragment>()
                                ),
                            }
                        );
                    })
                );
            })
        );
    }

    [Test]
    public void Registration_FactoryNull_ThrowsOnMaterialization()
    {
        Should.Throw<InvalidOperationException>(() =>
            ConfiglueApp.CreateContext(builder =>
            {
                builder.Add<AppSettings>(model =>
                    model.Sources(sources =>
                    {
                        sources.FromKubernetes(
                            new KubernetesSourceOptions
                            {
                                Kind = KubernetesResourceKind.ConfigMap,
                                Namespace = "app-config",
                                Name = "settings",
                                Key = "appsettings.json",
                                ClientFactory = _ => null!,
                                Codec = StateCodecBinding.Typed(
                                    new JsonStateCodec<AppSettings.Fragment>()
                                ),
                            }
                        );
                    })
                );
            })
        );
    }

    [Test]
    public async Task ConfigMapAndSecret_BackTypedStateThroughCodec()
    {
        var client = new FakeKubernetesObjectClient();
        var payload = EncodeFragment(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(9) }
        );
        client.SeedConfigMap(
            "app-config",
            "settings",
            "11",
            new Dictionary<string, string> { ["appsettings.json"] = payload },
            new Dictionary<string, byte[]>()
        );

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                {
                    sources.FromKubernetesConfigMap(
                        "app-config",
                        "settings",
                        "appsettings.json",
                        StateCodecBinding.Typed(new JsonStateCodec<AppSettings.Fragment>()),
                        client
                    );
                })
            );
        });

        var state = context.GetRuntimeState<AppSettings>();
        (await state.GetValueAsync()).RetryCount.ShouldBe(9);
        var diagnostics = state.GetDiagnostics();
        diagnostics.Sources.Count.ShouldBe(1);
        (diagnostics.Sources[0].PhysicalOrigin ?? string.Empty).ShouldContain(
            "k8s:configmap:app-config/settings"
        );
    }

    private static string EncodeFragment(AppSettings.Fragment fragment)
    {
        var codec = new JsonStateCodec<AppSettings.Fragment>();
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        codec.Serialize(fragment, buffer, new StateCodecContext());
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static ConfiglueResourceContext CreateContext() => ConfiglueResourceContext.Default;

    private static ValueTask<ResourceReadResult> ExpectedReadAsync(
        byte[] content,
        string? revision
    ) => ValueTaskCompat.FromResult(ResourceReadResult.Success(content, revision));

    private sealed record FakeSubject(SubjectKey Key) : IConfiglueSubject;

    private static class TestStreams
    {
        public static async IAsyncEnumerable<T> Of<T>(params T[] events)
        {
            foreach (var watchEvent in events)
            {
                await Task.Yield();
                yield return watchEvent;
            }
        }

        public static async IAsyncEnumerable<T> Empty<T>()
        {
            await Task.Yield();
            yield break;
        }

        public static async IAsyncEnumerable<T> Never<T>(
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            yield break;
        }

        public static async IAsyncEnumerable<T> Expired<T>()
        {
            await Task.Yield();
            await Task.FromException(
                new KubernetesResourceExpiredException("The watch resourceVersion is expired.")
            );
            yield break;
        }
    }

    private sealed class FakeKubernetesObjectClient : IKubernetesObjectClient
    {
        private readonly Dictionary<(string Namespace, string Name), ConfigMapState> _configMaps =
            new();
        private readonly Dictionary<(string Namespace, string Name), SecretState> _secrets = new();
        private long _revision = 100;

        public Func<
            string,
            string,
            string?,
            CancellationToken,
            IAsyncEnumerable<KubernetesWatchEvent>
        >? WatchHandler { get; set; }

        public int WatchCalls { get; private set; }

        public string? LastWatchResourceVersion { get; private set; }

        public void SeedConfigMap(
            string @namespace,
            string name,
            string resourceVersion,
            Dictionary<string, string> data,
            Dictionary<string, byte[]> binaryData,
            bool immutable = false
        ) =>
            _configMaps[(@namespace, name)] = new ConfigMapState(
                resourceVersion,
                immutable,
                new Dictionary<string, string>(data, StringComparer.Ordinal),
                new Dictionary<string, byte[]>(binaryData, StringComparer.Ordinal)
            );

        public void SeedSecret(
            string @namespace,
            string name,
            string resourceVersion,
            Dictionary<string, byte[]> data,
            bool immutable = false
        ) =>
            _secrets[(@namespace, name)] = new SecretState(
                resourceVersion,
                immutable,
                new Dictionary<string, byte[]>(data, StringComparer.Ordinal)
            );

        public void DeleteSecret(string @namespace, string name) =>
            _secrets.Remove((@namespace, name));

        public ConfigMapState GetConfigMapState(string @namespace, string name) =>
            _configMaps[(@namespace, name)];

        public SecretState GetSecretState(string @namespace, string name) =>
            _secrets[(@namespace, name)];

        public Task<KubernetesConfigMapSnapshot> GetConfigMapAsync(
            string @namespace,
            string name,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_configMaps.TryGetValue((@namespace, name), out var state))
            {
                return Task.FromException<KubernetesConfigMapSnapshot>(
                    new KubernetesObjectNotFoundException(
                        $"The ConfigMap '{@namespace}/{name}' was not found."
                    )
                );
            }

            return Task.FromResult(
                new KubernetesConfigMapSnapshot(
                    state.ResourceVersion,
                    state.Immutable,
                    new Dictionary<string, string>(state.Data, StringComparer.Ordinal),
                    new Dictionary<string, byte[]>(state.BinaryData, StringComparer.Ordinal)
                )
            );
        }

        public Task<KubernetesSecretSnapshot> GetSecretAsync(
            string @namespace,
            string name,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_secrets.TryGetValue((@namespace, name), out var state))
            {
                return Task.FromException<KubernetesSecretSnapshot>(
                    new KubernetesObjectNotFoundException(
                        $"The Secret '{@namespace}/{name}' was not found."
                    )
                );
            }

            return Task.FromResult(
                new KubernetesSecretSnapshot(
                    state.ResourceVersion,
                    state.Immutable,
                    new Dictionary<string, byte[]>(state.Data, StringComparer.Ordinal)
                )
            );
        }

        public Task<string?> ReplaceConfigMapAsync(
            string @namespace,
            string name,
            IReadOnlyDictionary<string, string> data,
            IReadOnlyDictionary<string, byte[]> binaryData,
            string? expectedResourceVersion,
            bool requireMissing,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            var exists = _configMaps.TryGetValue((@namespace, name), out var current);
            if (current?.Immutable == true)
            {
                return Task.FromException<string?>(
                    new KubernetesImmutableException(
                        $"The ConfigMap '{@namespace}/{name}' is immutable."
                    )
                );
            }

            if (requireMissing && exists)
            {
                return Task.FromException<string?>(
                    new KubernetesConflictException(
                        $"The ConfigMap '{@namespace}/{name}' already exists."
                    )
                );
            }

            if (
                expectedResourceVersion is not null
                && (!exists || current!.ResourceVersion != expectedResourceVersion)
            )
            {
                return Task.FromException<string?>(
                    new KubernetesConflictException(
                        $"The ConfigMap '{@namespace}/{name}' changed after it was read."
                    )
                );
            }

            var revision = Interlocked
                .Increment(ref _revision)
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
            _configMaps[(@namespace, name)] = new ConfigMapState(
                revision,
                current?.Immutable ?? false,
                new Dictionary<string, string>(
                    data.ToDictionary(static entry => entry.Key, static entry => entry.Value),
                    StringComparer.Ordinal
                ),
                new Dictionary<string, byte[]>(
                    binaryData.ToDictionary(static entry => entry.Key, static entry => entry.Value),
                    StringComparer.Ordinal
                )
            );
            return Task.FromResult<string?>(revision);
        }

        public Task<string?> ReplaceSecretAsync(
            string @namespace,
            string name,
            IReadOnlyDictionary<string, byte[]> data,
            string? expectedResourceVersion,
            bool requireMissing,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            var exists = _secrets.TryGetValue((@namespace, name), out var current);
            if (current?.Immutable == true)
            {
                return Task.FromException<string?>(
                    new KubernetesImmutableException(
                        $"The Secret '{@namespace}/{name}' is immutable."
                    )
                );
            }

            if (requireMissing && exists)
            {
                return Task.FromException<string?>(
                    new KubernetesConflictException(
                        $"The Secret '{@namespace}/{name}' already exists."
                    )
                );
            }

            if (
                expectedResourceVersion is not null
                && (!exists || current!.ResourceVersion != expectedResourceVersion)
            )
            {
                return Task.FromException<string?>(
                    new KubernetesConflictException(
                        $"The Secret '{@namespace}/{name}' changed after it was read."
                    )
                );
            }

            var revision = Interlocked
                .Increment(ref _revision)
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
            _secrets[(@namespace, name)] = new SecretState(
                revision,
                current?.Immutable ?? false,
                new Dictionary<string, byte[]>(
                    data.ToDictionary(static entry => entry.Key, static entry => entry.Value),
                    StringComparer.Ordinal
                )
            );
            return Task.FromResult<string?>(revision);
        }

        public IAsyncEnumerable<KubernetesWatchEvent> WatchConfigMapAsync(
            string @namespace,
            string name,
            string? resourceVersion,
            CancellationToken cancellationToken
        ) => WatchAsync(@namespace, name, resourceVersion, cancellationToken);

        public IAsyncEnumerable<KubernetesWatchEvent> WatchSecretAsync(
            string @namespace,
            string name,
            string? resourceVersion,
            CancellationToken cancellationToken
        ) => WatchAsync(@namespace, name, resourceVersion, cancellationToken);

        private IAsyncEnumerable<KubernetesWatchEvent> WatchAsync(
            string @namespace,
            string name,
            string? resourceVersion,
            CancellationToken cancellationToken
        )
        {
            WatchCalls++;
            LastWatchResourceVersion = resourceVersion;
            if (WatchHandler is not null)
            {
                return WatchHandler(@namespace, name, resourceVersion, cancellationToken);
            }

            return TestStreams.Never<KubernetesWatchEvent>(cancellationToken);
        }

        public sealed class ConfigMapState(
            string resourceVersion,
            bool immutable,
            Dictionary<string, string> data,
            Dictionary<string, byte[]> binaryData
        )
        {
            public string ResourceVersion { get; } = resourceVersion;

            public bool Immutable { get; } = immutable;

            public Dictionary<string, string> Data { get; } = data;

            public Dictionary<string, byte[]> BinaryData { get; } = binaryData;
        }

        public sealed class SecretState(
            string resourceVersion,
            bool immutable,
            Dictionary<string, byte[]> data
        )
        {
            public string ResourceVersion { get; } = resourceVersion;

            public bool Immutable { get; } = immutable;

            public Dictionary<string, byte[]> Data { get; } = data;
        }
    }
}
