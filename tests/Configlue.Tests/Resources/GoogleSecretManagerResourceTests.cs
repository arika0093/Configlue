using System.Buffers;
using System.Text;
using System.Text.Json;
using Configlue.Provider.Json;
using Configlue.Resource.GoogleSecretManager;

namespace Configlue.Tests;

public sealed class GoogleSecretManagerResourceTests
{
    [Test]
    public async Task ExplicitVersionReadReturnsPayloadAndNumericRevision()
    {
        var client = new FakeSecretManagerClient
        {
            AccessHandler = name =>
                new GoogleSecretAccessResult(
                    new byte[] { 1, 2, 3 },
                    name,
                    "3"
                ),
        };
        using var resource = new GoogleSecretManagerResource(
            client,
            "demo-project",
            "settings",
            new GoogleSecretManagerResourceOptions { Version = "3" }
        );

        var result = await resource.ReadAsync();

        result.Status.ShouldBe(StateReadStatus.Success);
        result.Content.ToArray().ShouldBe(new byte[] { 1, 2, 3 });
        result.Revision.ShouldBe("3");
        client.AccessedNames.ShouldBe(
            ["projects/demo-project/secrets/settings/versions/3"]
        );
    }

    [Test]
    public async Task LatestAliasResolvesToNumericRevision()
    {
        var client = new FakeSecretManagerClient
        {
            AccessHandler = _ => new GoogleSecretAccessResult(
                Encoding.UTF8.GetBytes("payload"),
                "projects/demo-project/secrets/settings/versions/9",
                "9"
            ),
        };
        using var resource = new GoogleSecretManagerResource(
            client,
            "demo-project",
            "settings"
        );

        var result = await resource.ReadAsync();

        result.Status.ShouldBe(StateReadStatus.Success);
        result.Revision.ShouldBe("9");
        client.AccessedNames.ShouldBe(
            ["projects/demo-project/secrets/settings/versions/latest"]
        );
    }

    [Test]
    public async Task CustomAliasAndRegionalLocationShapeResourceNames()
    {
        var client = new FakeSecretManagerClient
        {
            AccessHandler = name => new GoogleSecretAccessResult(
                new byte[] { 7 },
                "projects/demo-project/locations/eu/secrets/settings/versions/4",
                "4"
            ),
        };
        using var resource = new GoogleSecretManagerResource(
            client,
            "demo-project",
            "settings",
            new GoogleSecretManagerResourceOptions
            {
                Location = "eu",
                Version = "prod",
            }
        );

        var result = await resource.ReadAsync();

        result.Status.ShouldBe(StateReadStatus.Success);
        result.Revision.ShouldBe("4");
        client.AccessedNames.ShouldBe(
            ["projects/demo-project/locations/eu/secrets/settings/versions/prod"]
        );
        GoogleSecretManagerResource
            .BuildParentName("demo-project", "eu", "settings")
            .ShouldBe("projects/demo-project/locations/eu/secrets/settings");
        GoogleSecretManagerResource
            .BuildParentName("demo-project", null, "settings")
            .ShouldBe("projects/demo-project/secrets/settings");
    }

    [Test]
    public async Task PipelineReadPrefersBinaryContent()
    {
        var client = new FakeSecretManagerClient
        {
            AccessHandler = name => new GoogleSecretAccessResult(
                new byte[] { 10, 20, 30 },
                name,
                "5"
            ),
        };
        using var resource = new GoogleSecretManagerResource(client, "p", "s");

        // Measured (#295): small buffered secrets stay on the buffered path.
        resource.IsPipelineReadPreferred.ShouldBeFalse();
        await using var result = await resource.ReadPipelineAsync();
        var content = await result.ReadAllAsync();
        var bytes = new byte[3];
        content.CopyTo(bytes);
        result.Content!.AdvanceTo(content.End);

        result.Status.ShouldBe(StateReadStatus.Success);
        result.Revision.ShouldBe("5");
        bytes.ShouldBe(new byte[] { 10, 20, 30 });
    }

    [Test]
    public async Task BinaryPayloadsRoundTripWithoutTextAssumptions()
    {
        var payload = new byte[] { 0x00, 0xFF, 0x01, 0x02, 0x00, 0x7F };
        var client = new FakeSecretManagerClient
        {
            AccessHandler = name => new GoogleSecretAccessResult(payload, name, "1"),
        };
        using var resource = new GoogleSecretManagerResource(client, "p", "s");

        var direct = await resource.ReadAsync();
        await using var pipelined = await resource.ReadPipelineAsync();
        var sequence = await pipelined.ReadAllAsync();
        var roundTripped = new byte[payload.Length];
        sequence.CopyTo(roundTripped);
        pipelined.Content!.AdvanceTo(sequence.End);

        direct.Content.ToArray().ShouldBe(payload);
        roundTripped.ShouldBe(payload);
    }

    [Test]
    public async Task MissingSecretMapsToNotFound()
    {
        var client = new FakeSecretManagerClient
        {
            AccessError = new GoogleSecretNotFoundException(
                "The secret version 'projects/p/secrets/missing/versions/latest' was not found."
            ),
        };
        using var resource = new GoogleSecretManagerResource(client, "p", "missing");

        var direct = await resource.ReadAsync();
        await using var pipelined = await resource.ReadPipelineAsync();

        direct.Status.ShouldBe(StateReadStatus.NotFound);
        pipelined.Status.ShouldBe(StateReadStatus.NotFound);
    }

    [Test]
    public async Task DisabledAndDestroyedVersionsMapToNotFound()
    {
        foreach (
            var state in new[]
            {
                GoogleSecretVersionState.Disabled,
                GoogleSecretVersionState.Destroyed,
            }
        )
        {
            var client = new FakeSecretManagerClient
            {
                AccessError = new GoogleSecretVersionDisabledException(
                    $"The secret version is {state}."
                ),
            };
            using var resource = new GoogleSecretManagerResource(client, "p", "s");

            var result = await resource.ReadAsync();

            result.Status.ShouldBe(StateReadStatus.NotFound);
        }
    }

    [Test]
    public async Task SubjectAwareSelectorsResolveNamesAndClientsPerRoute()
    {
        var defaultClient = new FakeSecretManagerClient
        {
            AccessHandler = name => new GoogleSecretAccessResult(new byte[] { 1 }, name, "1"),
        };
        var routedClient = new FakeSecretManagerClient
        {
            AccessHandler = name => new GoogleSecretAccessResult(new byte[] { 2 }, name, "2"),
        };
        using var resource = new GoogleSecretManagerResource(
            defaultClient,
            "default-project",
            "default-secret",
            new GoogleSecretManagerResourceOptions
            {
                ProjectIdSelector = context => $"project-{context.Route.Value}",
                SecretIdSelector = context => $"{context.ResourceKey.Value}/secret",
                LocationSelector = _ => "us",
                VersionSelector = _ => "staging",
                ClientSelector = context =>
                    context.Route == RouteKey.From("primary") ? routedClient : defaultClient,
            }
        );
        var subject = new ResourceSubject("tenant-a");
        var context = new ConfiglueResourceContext(
            subject,
            ResourceKey.From(subject.Key),
            RouteKey.From("primary")
        );

        var result = await resource.ReadAsync(context);

        result.Content.ToArray().ShouldBe(new byte[] { 2 });
        routedClient.AccessedNames.ShouldBe(
            [
                $"projects/project-primary/locations/us/secrets/{context.ResourceKey.Value}/secret/versions/staging",
            ]
        );
        defaultClient.AccessCalls.ShouldBe(0);
        resource
            .GetResourceId(context)
            .ShouldNotBe(
                resource.GetResourceId(ConfiglueResourceContext.Default)
            );
    }

    [Test]
    public async Task AddVersionWritesAreOptInAndAppendNewVersions()
    {
        var client = new FakeSecretManagerClient
        {
            AddHandler = (_, _) =>
                new GoogleSecretVersionMetadata(
                    "projects/p/secrets/s/versions/8",
                    "8",
                    GoogleSecretVersionState.Enabled
                ),
        };
        using var readOnly = new GoogleSecretManagerResource(client, "p", "s");
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await readOnly.WriteAsync(new ResourceWriteRequest(new byte[] { 1 }))
        );
        client.AddCalls.ShouldBe(0);

        using var writable = new GoogleSecretManagerResource(
            client,
            "p",
            "s",
            new GoogleSecretManagerResourceOptions { EnableWrites = true }
        );
        var result = await writable.WriteAsync(
            new ResourceWriteRequest(new byte[] { 9, 9 })
        );

        result.Revision.ShouldBe("8");
        client.AddCalls.ShouldBe(1);
        client.LastParent.ShouldBe("projects/p/secrets/s");
        client.LastPayload!.ShouldBe(new byte[] { 9, 9 });
    }

    [Test]
    public async Task ConditionalWritesGetBestEffortBaselineChecks()
    {
        var currentVersion = "7";
        var client = new FakeSecretManagerClient
        {
            MetadataHandler = name =>
                new GoogleSecretVersionMetadata(
                    name,
                    currentVersion,
                    GoogleSecretVersionState.Enabled
                ),
            AddHandler = (_, _) =>
                new GoogleSecretVersionMetadata(
                    "projects/p/secrets/s/versions/8",
                    "8",
                    GoogleSecretVersionState.Enabled
                ),
        };
        using var resource = new GoogleSecretManagerResource(
            client,
            "p",
            "s",
            new GoogleSecretManagerResourceOptions { EnableWrites = true }
        );

        var written = await resource.WriteAsync(
            new ResourceWriteRequest(new byte[] { 1 }, Condition: RevisionCondition.Match("7"))
        );
        written.Revision.ShouldBe("8");

        currentVersion = "8";
        await Should.ThrowAsync<StateConflictException>(async () =>
            await resource.WriteAsync(
                new ResourceWriteRequest(
                    new byte[] { 2 },
                    Condition: RevisionCondition.Match("7")
                )
            )
        );
        await Should.ThrowAsync<StateConflictException>(async () =>
            await resource.WriteAsync(
                new ResourceWriteRequest(
                    new byte[] { 3 },
                    Condition: RevisionCondition.MustNotExist
                )
            )
        );
        client.AddCalls.ShouldBe(1);
    }

    [Test]
    public async Task MustNotExistWritesSucceedWhenAliasIsMissing()
    {
        var client = new FakeSecretManagerClient
        {
            MetadataError = new GoogleSecretNotFoundException("The alias was deleted."),
            AddHandler = (_, _) =>
                new GoogleSecretVersionMetadata(
                    "projects/p/secrets/s/versions/1",
                    "1",
                    GoogleSecretVersionState.Enabled
                ),
        };
        using var resource = new GoogleSecretManagerResource(
            client,
            "p",
            "s",
            new GoogleSecretManagerResourceOptions { EnableWrites = true }
        );

        var written = await resource.WriteAsync(
            new ResourceWriteRequest(new byte[] { 1 }, Condition: RevisionCondition.MustNotExist)
        );

        written.Revision.ShouldBe("1");
    }

    [Test]
    public async Task PollingDetectsAliasMovesWithMetadataOnly()
    {
        var currentVersion = "1";
        var client = new FakeSecretManagerClient
        {
            MetadataHandler = name =>
                new GoogleSecretVersionMetadata(name, currentVersion, GoogleSecretVersionState.Enabled),
        };
        using var resource = new GoogleSecretManagerResource(
            client,
            "p",
            "s",
            new GoogleSecretManagerResourceOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(50),
            }
        );

        var wait = resource.WaitForChangeAsync("1").AsTask();
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        wait.IsCompleted.ShouldBeFalse();
        currentVersion = "2";
        await wait.WaitAsync(TimeSpan.FromSeconds(30));

        client.AccessCalls.ShouldBe(0);
        (client.MetadataCalls >= 2).ShouldBeTrue();
    }

    [Test]
    public async Task PollingReturnsWhenAliasBecomesMissingOrDisabled()
    {
        var missingClient = new FakeSecretManagerClient
        {
            MetadataError = new GoogleSecretNotFoundException("The alias was deleted."),
        };
        using var missingResource = new GoogleSecretManagerResource(
            missingClient,
            "p",
            "s",
            new GoogleSecretManagerResourceOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(50),
            }
        );
        await missingResource.WaitForChangeAsync("4").AsTask().WaitAsync(TimeSpan.FromSeconds(30));

        var disabledClient = new FakeSecretManagerClient
        {
            MetadataHandler = name =>
                new GoogleSecretVersionMetadata(name, "4", GoogleSecretVersionState.Disabled),
        };
        using var disabledResource = new GoogleSecretManagerResource(
            disabledClient,
            "p",
            "s",
            new GoogleSecretManagerResourceOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(50),
            }
        );
        await disabledResource.WaitForChangeAsync("4").AsTask().WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Test]
    public async Task PollingSurvivesTransientMetadataFailures()
    {
        var attempts = 0;
        var client = new FakeSecretManagerClient
        {
            MetadataHandler = name =>
            {
                if (Interlocked.Increment(ref attempts) <= 2)
                {
                    throw new GoogleSecretUnavailableException("The backend is unavailable.");
                }

                return new GoogleSecretVersionMetadata(name, "6", GoogleSecretVersionState.Enabled);
            },
        };
        using var resource = new GoogleSecretManagerResource(
            client,
            "p",
            "s",
            new GoogleSecretManagerResourceOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(50),
            }
        );

        await resource.WaitForChangeAsync("5").AsTask().WaitAsync(TimeSpan.FromSeconds(30));

        (client.MetadataCalls >= 3).ShouldBeTrue();
    }

    [Test]
    public async Task TransientReadFailuresPropagateAsUnavailable()
    {
        var client = new FakeSecretManagerClient
        {
            AccessError = new GoogleSecretUnavailableException(
                "The Secret Manager backend is temporarily unavailable."
            ),
        };
        using var resource = new GoogleSecretManagerResource(client, "p", "s");

        await Should.ThrowAsync<GoogleSecretUnavailableException>(async () =>
            await resource.ReadAsync()
        );
    }

    [Test]
    public async Task FixedVersionsNeverPollAndOnlyEndOnCancelOrDispose()
    {
        var client = new FakeSecretManagerClient
        {
            MetadataHandler = name =>
                new GoogleSecretVersionMetadata(name, "3", GoogleSecretVersionState.Enabled),
            AccessHandler = name => new GoogleSecretAccessResult(new byte[] { 1 }, name, "3"),
        };
        using var resource = new GoogleSecretManagerResource(
            client,
            "p",
            "s",
            new GoogleSecretManagerResourceOptions
            {
                Version = "3",
                PollInterval = TimeSpan.FromMilliseconds(50),
            }
        );

        using var source = new CancellationTokenSource();
        var wait = resource.WaitForChangeAsync("3", source.Token).AsTask();
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        wait.IsCompleted.ShouldBeFalse();
        client.MetadataCalls.ShouldBe(0);
        client.AccessCalls.ShouldBe(0);

        source.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () => await wait);
    }

    [Test]
    public async Task DisposeWakesAliasWaiters()
    {
        var client = new FakeSecretManagerClient
        {
            MetadataHandler = name =>
                new GoogleSecretVersionMetadata(name, "1", GoogleSecretVersionState.Enabled),
        };
        var resource = new GoogleSecretManagerResource(
            client,
            "p",
            "s",
            new GoogleSecretManagerResourceOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(50),
            }
        );

        var wait = resource.WaitForChangeAsync("1").AsTask();
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        resource.Dispose();
        await wait.WaitAsync(TimeSpan.FromSeconds(30));
        await Should.ThrowAsync<ObjectDisposedException>(async () =>
            await resource.WaitForChangeAsync("1")
        );
        resource.Dispose();
    }

    [Test]
    public async Task WaitingHonorsCancellation()
    {
        var client = new FakeSecretManagerClient
        {
            MetadataHandler = name =>
                new GoogleSecretVersionMetadata(name, "1", GoogleSecretVersionState.Enabled),
        };
        using var resource = new GoogleSecretManagerResource(
            client,
            "p",
            "s",
            new GoogleSecretManagerResourceOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(50),
            }
        );

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await resource.WaitForChangeAsync("1", canceled.Token)
        );
    }

    [Test]
    public async Task TypedStateFlowsThroughTheCodecPipeline()
    {
        var codec = new JsonStateCodec<AppSettings.Fragment>();
        var payload = Encode(
            codec,
            new AppSettings.Fragment { Label = Optional<string?>.Present("from-gcp") }
        );
        var client = new FakeSecretManagerClient
        {
            AccessHandler = name => new GoogleSecretAccessResult(payload, name, "11"),
        };
        using var resource = new GoogleSecretManagerResource(client, "p", "s");
        var source = new SerializedSource<AppSettings.Fragment>(
            resource,
            StateCodecBinding.Typed(codec)
        );

        var result = await source.ReadAsync();

        result.Status.ShouldBe(StateReadStatus.Success);
        result.Revision.ShouldBe("11");
        result.Value!.Label.Value.ShouldBe("from-gcp");
    }

    [Test]
    public async Task MalformedPayloadsSurfaceCodecErrorsWithoutSecretContent()
    {
        const string marker = "malformed-payload-marker-7f3a";
        var client = new FakeSecretManagerClient
        {
            AccessHandler = name =>
                new GoogleSecretAccessResult(
                    Encoding.UTF8.GetBytes("{ not-json " + marker),
                    name,
                    "7"
                ),
        };
        using var resource = new GoogleSecretManagerResource(client, "p", "s");
        var source = new SerializedSource<AppSettings.Fragment>(
            resource,
            StateCodecBinding.Typed(new JsonStateCodec<AppSettings.Fragment>())
        );

        var exception = await Should.ThrowAsync<JsonException>(async () =>
            await source.ReadAsync()
        );
        exception.Message.ShouldNotContain(marker);
    }

    [Test]
    public async Task SecretContentsTokensAndCredentialsStayOutOfDiagnostics()
    {
        const string secretMarker = "super-secret-payload-marker-9c1e";
        const string tokenMarker = "ya29.credential-token-marker-4bd2";
        var client = new FakeSecretManagerClient
        {
            AccessHandler = _ => new GoogleSecretAccessResult(
                Encoding.UTF8.GetBytes(secretMarker),
                "projects/p/secrets/s/versions/1",
                "1"
            ),
        };
        using var resource = new GoogleSecretManagerResource(client, "p", "s");

        var read = await resource.ReadAsync();
        read.Content.ToArray().ShouldBe(Encoding.UTF8.GetBytes(secretMarker));
        resource.GetResourceId(ConfiglueResourceContext.Default).Value.ShouldNotContain(secretMarker);

        using var readOnly = new GoogleSecretManagerResource(
            new FakeSecretManagerClient(),
            "p",
            "s"
        );
        var writeError = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await readOnly.WriteAsync(
                new ResourceWriteRequest(Encoding.UTF8.GetBytes(secretMarker))
            )
        );
        writeError.Message.ShouldNotContain(secretMarker);
        writeError.Message.ShouldNotContain(tokenMarker);

        using var writable = new GoogleSecretManagerResource(
            new FakeSecretManagerClient
            {
                MetadataHandler = name =>
                    new GoogleSecretVersionMetadata(name, "2", GoogleSecretVersionState.Enabled),
            },
            "p",
            "s",
            new GoogleSecretManagerResourceOptions { EnableWrites = true }
        );
        var conditionError = await Should.ThrowAsync<StateConflictException>(async () =>
            await writable.WriteAsync(
                new ResourceWriteRequest(
                    Encoding.UTF8.GetBytes(secretMarker + tokenMarker),
                    Condition: RevisionCondition.Match("1")
                )
            )
        );
        conditionError.Message.ShouldNotContain(secretMarker);
        conditionError.Message.ShouldNotContain(tokenMarker);
    }

    [Test]
    public void ResourceIdIsStableAndScopedToSecretAndSelector()
    {
        var first = new GoogleSecretManagerResource(new FakeSecretManagerClient(), "p", "s");
        var same = new GoogleSecretManagerResource(new FakeSecretManagerClient(), "p", "s");
        var otherSecret = new GoogleSecretManagerResource(new FakeSecretManagerClient(), "p", "other");
        var otherVersion = new GoogleSecretManagerResource(
            new FakeSecretManagerClient(),
            "p",
            "s",
            new GoogleSecretManagerResourceOptions { Version = "2" }
        );
        var overridden = new ResourceId("deployment:settings");
        var withOverride = new GoogleSecretManagerResource(
            new FakeSecretManagerClient(),
            "p",
            "s",
            new GoogleSecretManagerResourceOptions { FixedResourceId = overridden }
        );

        var context = ConfiglueResourceContext.Default;
        same.GetResourceId(context).ShouldBe(first.GetResourceId(context));
        otherSecret.GetResourceId(context).ShouldNotBe(first.GetResourceId(context));
        otherVersion.GetResourceId(context).ShouldNotBe(first.GetResourceId(context));
        withOverride.GetResourceId(context).ShouldBe(overridden);
    }

    [Test]
    public void ConstructorsRejectInvalidIdentifiers()
    {
        Should.Throw<ArgumentNullException>(() =>
            new GoogleSecretManagerResource(null!, "p", "s")
        );
        Should.Throw<ArgumentException>(() =>
            new GoogleSecretManagerResource(new FakeSecretManagerClient(), " ", "s")
        );
        Should.Throw<ArgumentException>(() =>
            new GoogleSecretManagerResource(new FakeSecretManagerClient(), "p", " ")
        );
        Should.Throw<InvalidOperationException>(() =>
            new GoogleSecretManagerResource(
                new FakeSecretManagerClient(),
                "p",
                "s",
                new GoogleSecretManagerResourceOptions
                {
                    PollInterval = TimeSpan.Zero,
                }
            )
        );
        GoogleSecretManagerResource.IsFixedNumericVersion("12").ShouldBeTrue();
        GoogleSecretManagerResource.IsFixedNumericVersion("latest").ShouldBeFalse();
        GoogleSecretManagerResource.IsFixedNumericVersion("prod").ShouldBeFalse();
        GoogleSecretManagerResource.IsFixedNumericVersion("1a").ShouldBeFalse();
    }

    [Test]
    public async Task RegistrationReadsAndWritesTypedState()
    {
        var codec = new JsonStateCodec<AppSettings.Fragment>();
        var payload = Encode(
            codec,
            new AppSettings.Fragment { Label = Optional<string?>.Present("registered") }
        );
        var client = new FakeSecretManagerClient
        {
            AccessHandler = name => new GoogleSecretAccessResult(payload, name, "3"),
            MetadataHandler = name =>
                new GoogleSecretVersionMetadata(name, "3", GoogleSecretVersionState.Enabled),
            AddHandler = (_, _) =>
                new GoogleSecretVersionMetadata(
                    "projects/p/secrets/s/versions/4",
                    "4",
                    GoogleSecretVersionState.Enabled
                ),
        };

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                {
                    sources.FromGoogleSecretManager(
                        new GoogleSecretManagerSourceOptions
                        {
                            ProjectId = "p",
                            SecretId = "s",
                            Client = client,
                            Codec = StateCodecBinding.Typed(codec),
                            Writable = true,
                        }
                    );
                })
            );
        });

        var options = (IConfiglueRuntimeState<AppSettings>)context.GetState<AppSettings>();
        (await options.GetValueAsync()).Label.ShouldBe("registered");

        await options.SaveAsync(settings => settings.Label = "written");
        client.AddCalls.ShouldBe(1);
        DecodeLabel(codec, client.LastPayload!).ShouldBe("written");
    }

    [Test]
    public async Task RegistrationResolvesClientsFromDependencyInjectionForAdc()
    {
        var codec = new JsonStateCodec<AppSettings.Fragment>();
        var payload = Encode(
            codec,
            new AppSettings.Fragment { Label = Optional<string?>.Present("adc") }
        );
        var client = new FakeSecretManagerClient
        {
            AccessHandler = name => new GoogleSecretAccessResult(payload, name, "1"),
        };
        var factoryInvoked = false;

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                {
                    sources.FromGoogleSecretManager(
                        new GoogleSecretManagerSourceOptions
                        {
                            ProjectId = "p",
                            SecretId = "s",
                            ClientFactory = _ =>
                            {
                                factoryInvoked = true;
                                return client;
                            },
                            Codec = StateCodecBinding.Typed(codec),
                        }
                    );
                })
            );
        });

        (await context.GetState<AppSettings>().GetValueAsync()).Label.ShouldBe("adc");
        factoryInvoked.ShouldBeTrue();
        client.AccessCalls.ShouldBe(1);
    }

    [Test]
    public void RegistrationRequiresExactlyOneClientSource()
    {
        var codec = StateCodecBinding.Typed(new JsonStateCodec<AppSettings.Fragment>());
        var builder = new ConfiglueSourceSetBuilder();
        var client = new FakeSecretManagerClient();

        Should.Throw<ArgumentException>(() =>
            builder.FromGoogleSecretManager(
                new GoogleSecretManagerSourceOptions
                {
                    ProjectId = "p",
                    SecretId = "s",
                    Codec = codec,
                }
            )
        );
        Should.Throw<ArgumentException>(() =>
            builder.FromGoogleSecretManager(
                new GoogleSecretManagerSourceOptions
                {
                    ProjectId = "p",
                    SecretId = "s",
                    Client = client,
                    ClientFactory = _ => client,
                    Codec = codec,
                }
            )
        );
    }

    [Test]
    public void RegistrationRejectsNullClientFactoryResults()
    {
        Should.Throw<InvalidOperationException>(() =>
            ConfiglueApp.CreateContext(builder =>
            {
                builder.Add<AppSettings>(model =>
                    model.Sources(sources =>
                    {
                        sources.FromGoogleSecretManager(
                            new GoogleSecretManagerSourceOptions
                            {
                                ProjectId = "p",
                                SecretId = "s",
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
    public void RegistrationNeverWatchesFixedVersionsOrDisabledPolling()
    {
        GoogleSecretManagerSourceRegistration
            .ShouldWatch(
                new GoogleSecretManagerResourceOptions { Version = "3", EnableWatching = true }
            )
            .ShouldBeFalse();
        GoogleSecretManagerSourceRegistration
            .ShouldWatch(new GoogleSecretManagerResourceOptions { Version = "latest" })
            .ShouldBeFalse();
        GoogleSecretManagerSourceRegistration
            .ShouldWatch(
                new GoogleSecretManagerResourceOptions
                {
                    Version = "latest",
                    EnableWatching = true,
                }
            )
            .ShouldBeTrue();
        GoogleSecretManagerSourceRegistration
            .ShouldWatch(
                new GoogleSecretManagerResourceOptions
                {
                    Version = "latest",
                    EnableWatching = true,
                    VersionSelector = _ => "3",
                }
            )
            .ShouldBeTrue();
    }

    private static byte[] Encode(
        JsonStateCodec<AppSettings.Fragment> codec,
        AppSettings.Fragment fragment
    )
    {
        var destination = new ArrayBufferWriter<byte>();
        var context = default(StateCodecContext);
        codec.Serialize(fragment, destination, in context);
        return destination.WrittenMemory.ToArray();
    }

    private static string? DecodeLabel(
        JsonStateCodec<AppSettings.Fragment> codec,
        byte[] payload
    )
    {
        var sequence = new ReadOnlySequence<byte>(payload);
        var context = default(StateCodecContext);
        return codec.Deserialize(in sequence, in context)?.Label.Value;
    }

    private sealed record ResourceSubject(string Name) : IConfiglueSubject
    {
        public SubjectKey Key => SubjectKey.From(Name);
    }

    private sealed class FakeSecretManagerClient : IGoogleSecretManagerClient
    {
        private readonly object _gate = new();
        private int _accessCalls;
        private int _metadataCalls;
        private int _addCalls;
        private readonly List<string> _accessed = [];
        private readonly List<string> _metadata = [];

        public int AccessCalls => Volatile.Read(ref _accessCalls);

        public int MetadataCalls => Volatile.Read(ref _metadataCalls);

        public int AddCalls => Volatile.Read(ref _addCalls);

        public IReadOnlyList<string> AccessedNames
        {
            get
            {
                lock (_gate)
                {
                    return _accessed.ToArray();
                }
            }
        }

        public string? LastParent { get; private set; }

        public byte[]? LastPayload { get; private set; }

        public Func<string, GoogleSecretAccessResult>? AccessHandler { get; set; }

        public Exception? AccessError { get; set; }

        public Func<string, GoogleSecretVersionMetadata>? MetadataHandler { get; set; }

        public Exception? MetadataError { get; set; }

        public Func<string, byte[], GoogleSecretVersionMetadata>? AddHandler { get; set; }

        public Task<GoogleSecretAccessResult> AccessSecretVersionAsync(
            string versionedName,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _accessCalls);
            lock (_gate)
            {
                _accessed.Add(versionedName);
            }

            if (AccessError is not null)
            {
                return Task.FromException<GoogleSecretAccessResult>(AccessError);
            }

            return Task.FromResult(
                AccessHandler is null
                    ? throw new GoogleSecretNotFoundException(
                        $"The secret version '{versionedName}' was not found."
                    )
                    : AccessHandler(versionedName)
            );
        }

        public Task<GoogleSecretVersionMetadata> GetSecretVersionAsync(
            string versionedName,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _metadataCalls);
            lock (_gate)
            {
                _metadata.Add(versionedName);
            }

            if (MetadataError is not null)
            {
                return Task.FromException<GoogleSecretVersionMetadata>(MetadataError);
            }

            return Task.FromResult(
                MetadataHandler is null
                    ? throw new GoogleSecretNotFoundException(
                        $"The secret version '{versionedName}' was not found."
                    )
                    : MetadataHandler(versionedName)
            );
        }

        public Task<GoogleSecretVersionMetadata> AddSecretVersionAsync(
            string parent,
            ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _addCalls);
            lock (_gate)
            {
                LastParent = parent;
                LastPayload = payload.ToArray();
            }

            return Task.FromResult(
                AddHandler is null
                    ? new GoogleSecretVersionMetadata(
                        $"{parent}/versions/1",
                        "1",
                        GoogleSecretVersionState.Enabled
                    )
                    : AddHandler(parent, payload.ToArray())
            );
        }
    }
}
