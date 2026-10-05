using System.Buffers;
using System.Text;
using Amazon.Runtime;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Configlue.Provider.Json;
using Configlue.Resource.SecretsManager;

namespace Configlue.Tests;

public sealed class SecretsManagerResourceTests
{
    private const string SentinelValue = "S3CR3T-sentinel-payload-value";

    [Test]
    public async Task ReadSecretString_ReturnsUtf8ContentAndVersionRevision()
    {
        var client = new FakeSecretsManagerClient();
        client.GetHandler = (_, _, _, _) =>
            ValueTaskCompat.FromResult(
                new SecretsManagerSecretValue(
                    "\"" + SentinelValue + "\"",
                    null,
                    "version-1",
                    ["AWSCURRENT"],
                    "arn:aws:secretsmanager:us-east-1:123:secret:app",
                    "app"
                )
            );
        var resource = new SecretsManagerResource(client, "app");

        var result = await resource.ReadAsync();

        result.Status.ShouldBe(StateReadStatus.Success);
        Encoding.UTF8.GetString(result.Content.Span).ShouldBe("\"" + SentinelValue + "\"");
        result.Revision.ShouldBe("version-1");
        client.LastVersionStage.ShouldBe("AWSCURRENT");
        client.LastVersionId.ShouldBeNull();
        client.LastSecretId.ShouldBe("app");
    }

    [Test]
    public async Task ReadPipelineAsync_ReturnsSameContentAndRevision()
    {
        var client = new FakeSecretsManagerClient();
        client.GetHandler = (_, _, _, _) =>
            ValueTaskCompat.FromResult(
                new SecretsManagerSecretValue(
                    "\"pipelined\"",
                    null,
                    "version-7",
                    ["AWSCURRENT"],
                    null,
                    null
                )
            );
        var resource = new SecretsManagerResource(client, "app");

        // Measured (#295): small buffered secrets stay on the buffered path.
        resource.IsPipelineReadPreferred.ShouldBeFalse();
        await using var result = await resource.ReadPipelineAsync();
        var content = await result.ReadAllAsync();
        var bytes = content.ToArray();
        result.Content!.AdvanceTo(content.End);

        result.Status.ShouldBe(StateReadStatus.Success);
        Encoding.UTF8.GetString(bytes).ShouldBe("\"pipelined\"");
        result.Revision.ShouldBe("version-7");
    }

    [Test]
    public async Task ReadSecretBinary_ReturnsRawBytes()
    {
        var client = new FakeSecretsManagerClient();
        client.GetHandler = (_, _, _, _) =>
            ValueTaskCompat.FromResult(
                new SecretsManagerSecretValue(
                    null,
                    new ReadOnlyMemory<byte>([1, 2, 3, 4]),
                    "version-2",
                    ["AWSCURRENT"],
                    null,
                    null
                )
            );
        var resource = new SecretsManagerResource(client, "app");

        var result = await resource.ReadAsync();

        result.Status.ShouldBe(StateReadStatus.Success);
        result.Content.ToArray().ShouldBe(new byte[] { 1, 2, 3, 4 });
        result.Revision.ShouldBe("version-2");
    }

    [Test]
    public async Task FixedVersion_ReadsByVersionIdAndNeverWatches()
    {
        var client = new FakeSecretsManagerClient();
        client.GetHandler = (_, _, _, _) =>
            ValueTaskCompat.FromResult(
                new SecretsManagerSecretValue("\"pinned\"", null, "pinned-id", [], null, null)
            );
        using var resource = new SecretsManagerResource(
            client,
            "app",
            new SecretsManagerResourceOptions { VersionId = "pinned-id" }
        );

        resource.IsWatchSupported.ShouldBeFalse();
        var result = await resource.ReadAsync();

        result.Status.ShouldBe(StateReadStatus.Success);
        result.Revision.ShouldBe("pinned-id");
        client.LastVersionId.ShouldBe("pinned-id");
        client.LastVersionStage.ShouldBeNull();

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await resource.WaitForChangeAsync("pinned-id")
        );
    }

    [Test]
    public async Task CustomStagingLabel_ReadsByLabel()
    {
        var client = new FakeSecretsManagerClient();
        client.GetHandler = (_, _, _, _) =>
            ValueTaskCompat.FromResult(
                new SecretsManagerSecretValue("\"pending\"", null, "version-9", ["AWSPENDING"], null, null)
            );
        var resource = new SecretsManagerResource(
            client,
            "app",
            new SecretsManagerResourceOptions { VersionStage = "AWSPENDING" }
        );

        var result = await resource.ReadAsync();

        result.Revision.ShouldBe("version-9");
        client.LastVersionStage.ShouldBe("AWSPENDING");
        client.LastVersionId.ShouldBeNull();
    }

    [Test]
    public async Task SubjectAwareSelection_ResolvesSecretAndClientPerRoute()
    {
        var primary = new FakeSecretsManagerClient();
        primary.GetHandler = (_, _, _, _) =>
            ValueTaskCompat.FromResult(
                new SecretsManagerSecretValue("\"a\"", null, "va", ["AWSCURRENT"], null, null)
            );
        var secondary = new FakeSecretsManagerClient();
        secondary.GetHandler = (_, _, _, _) =>
            ValueTaskCompat.FromResult(
                new SecretsManagerSecretValue("\"b\"", null, "vb", ["AWSCURRENT"], null, null)
            );
        var routeA = RouteKey.From("primary");
        var routeB = RouteKey.From("secondary");
        var resource = new SecretsManagerResource(
            primary,
            "unused",
            new SecretsManagerResourceOptions
            {
                SecretIdSelector = context => $"app/{context.ResourceKey.Value}",
            },
            context => context.Route == routeA ? primary : secondary
        );
        var first = CreateContext("tenant-a", routeA);
        var second = CreateContext("tenant-a", routeB);

        (await resource.ReadAsync(first)).Revision.ShouldBe("va");
        (await resource.ReadAsync(second)).Revision.ShouldBe("vb");
        primary.LastSecretId.ShouldStartWith("app/");
        secondary.LastSecretId.ShouldBe(primary.LastSecretId);
        resource.GetResourceId(first).ShouldNotBe(resource.GetResourceId(second));
    }

    [Test]
    public async Task Read_MapsMissingSecretToNotFound()
    {
        var client = new FakeSecretsManagerClient();
        client.GetHandler = (_, _, _, _) =>
            ValueTaskCompat.FromException<SecretsManagerSecretValue>(
                new ResourceNotFoundException("Secrets Manager can't find the specified secret.")
            );
        var resource = new SecretsManagerResource(client, "missing");

        (await resource.ReadAsync()).Status.ShouldBe(StateReadStatus.NotFound);
    }

    [Test]
    public async Task Read_MapsDeletedSecretToNotFound()
    {
        var client = new FakeSecretsManagerClient();
        client.GetHandler = (_, _, _, _) =>
            ValueTaskCompat.FromException<SecretsManagerSecretValue>(
                new InvalidRequestException(
                    "You tried to access a secret that is scheduled for deletion."
                )
            );
        var resource = new SecretsManagerResource(client, "deleted");

        (await resource.ReadAsync()).Status.ShouldBe(StateReadStatus.NotFound);
    }

    [Test]
    public async Task Read_MapsDisabledKeyToUnavailable()
    {
        var client = new FakeSecretsManagerClient();
        client.GetHandler = (_, _, _, _) =>
            ValueTaskCompat.FromException<SecretsManagerSecretValue>(
                new DecryptionFailureException("The KMS key is disabled.")
            );
        var resource = new SecretsManagerResource(client, "app");

        (await resource.ReadAsync()).Status.ShouldBe(StateReadStatus.Unavailable);
    }

    [Test]
    public async Task Read_RetriesTransientFailuresThenSucceeds()
    {
        var client = new FakeSecretsManagerClient();
        var attempts = 0;
        client.GetHandler = (_, _, _, _) =>
        {
            attempts++;
            return attempts switch
            {
                1 => ValueTaskCompat.FromException<SecretsManagerSecretValue>(
                    new LimitExceededException("Rate exceeded.")
                ),
                2 => ValueTaskCompat.FromException<SecretsManagerSecretValue>(
                    new InternalServiceErrorException("Internal error.")
                ),
                _ => ValueTaskCompat.FromResult(
                    new SecretsManagerSecretValue("\"ok\"", null, "v-ok", ["AWSCURRENT"], null, null)
                ),
            };
        };
        var resource = new SecretsManagerResource(
            client,
            "app",
            new SecretsManagerResourceOptions
            {
                MaxRetryAttempts = 3,
                RetryBaseDelay = TimeSpan.FromMilliseconds(1),
            }
        );

        var result = await resource.ReadAsync();

        result.Status.ShouldBe(StateReadStatus.Success);
        result.Revision.ShouldBe("v-ok");
        attempts.ShouldBe(3);
    }

    [Test]
    public async Task Read_MapsPersistentThrottlingToUnavailable()
    {
        var client = new FakeSecretsManagerClient();
        client.GetHandler = (_, _, _, _) =>
            ValueTaskCompat.FromException<SecretsManagerSecretValue>(
                new LimitExceededException("Rate exceeded.")
            );
        var resource = new SecretsManagerResource(
            client,
            "app",
            new SecretsManagerResourceOptions
            {
                MaxRetryAttempts = 2,
                RetryBaseDelay = TimeSpan.FromMilliseconds(1),
            }
        );

        (await resource.ReadAsync()).Status.ShouldBe(StateReadStatus.Unavailable);
        client.GetCalls.ShouldBe(3);
    }

    [Test]
    public async Task Read_EmptyPayloadIsInvalidPayload()
    {
        var client = new FakeSecretsManagerClient();
        client.GetHandler = (_, _, _, _) =>
            ValueTaskCompat.FromResult(
                new SecretsManagerSecretValue(null, null, "version-3", [], null, null)
            );
        var resource = new SecretsManagerResource(client, "app");

        var result = await resource.ReadAsync();

        result.Status.ShouldBe(StateReadStatus.InvalidPayload);
        result.Revision.ShouldBe("version-3");
    }

    [Test]
    public async Task Write_AddsNewVersionWithConfiguredStages()
    {
        var client = new FakeSecretsManagerClient();
        client.PutHandler = (_, _, _, _, _, _) =>
            ValueTaskCompat.FromResult(
                new SecretsManagerPutResult("version-new", null, null, ["AWSCURRENT"])
            );
        var resource = new SecretsManagerResource(client, "app");

        var first = await resource.WriteAsync(
            new ResourceWriteRequest(Encoding.UTF8.GetBytes("\"secret-a\""))
        );
        var second = await resource.WriteAsync(
            new ResourceWriteRequest(Encoding.UTF8.GetBytes("\"secret-b\""))
        );

        first.Revision.ShouldBe("version-new");
        client.PutCalls.ShouldBe(2);
        client.LastPutAsBinary.ShouldBeFalse();
        Encoding.UTF8.GetString(client.LastPutContent.Span).ShouldBe("\"secret-b\"");
        client.LastPutStages.ShouldBe(["AWSCURRENT"]);
        client.RequestTokens.Count.ShouldBe(2);
        client.RequestTokens.Distinct().Count().ShouldBe(2);
    }

    [Test]
    public async Task Write_BinaryModePersistsSecretBinary()
    {
        var client = new FakeSecretsManagerClient();
        client.PutHandler = (_, _, _, _, _, _) =>
            ValueTaskCompat.FromResult(
                new SecretsManagerPutResult("version-bin", null, null, ["AWSCURRENT"])
            );
        var resource = new SecretsManagerResource(
            client,
            "app",
            new SecretsManagerResourceOptions { UseSecretBinary = true }
        );

        var result = await resource.WriteAsync(new ResourceWriteRequest(new byte[] { 9, 8, 7 }));

        result.Revision.ShouldBe("version-bin");
        client.LastPutAsBinary.ShouldBeTrue();
        client.LastPutContent.ToArray().ShouldBe(new byte[] { 9, 8, 7 });
    }

    [Test]
    public async Task Write_RejectsConditionalWritesWithoutAtomicGuarantee()
    {
        var client = new FakeSecretsManagerClient();
        var resource = new SecretsManagerResource(client, "app");
        var content = Encoding.UTF8.GetBytes("\"" + SentinelValue + "\"");

        var matchFailure = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await resource.WriteAsync(
                new ResourceWriteRequest(content, Condition: RevisionCondition.Match("version-1"))
            )
        );
        var missingFailure = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await resource.WriteAsync(
                new ResourceWriteRequest(content, Condition: RevisionCondition.MustNotExist)
            )
        );

        client.PutCalls.ShouldBe(0);
        matchFailure.Message.ShouldNotContain(SentinelValue);
        missingFailure.Message.ShouldNotContain(SentinelValue);
    }

    [Test]
    public async Task Write_MapsMissingSecretWithoutLeakingContent()
    {
        var client = new FakeSecretsManagerClient();
        client.PutHandler = (_, _, _, _, _, _) =>
            ValueTaskCompat.FromException<SecretsManagerPutResult>(
                new ResourceNotFoundException("Secrets Manager can't find the specified secret.")
            );
        var resource = new SecretsManagerResource(client, "app");

        var failure = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await resource.WriteAsync(
                new ResourceWriteRequest(Encoding.UTF8.GetBytes("\"" + SentinelValue + "\""))
            )
        );

        failure.Message.ShouldNotContain(SentinelValue);
    }

    [Test]
    public async Task PromoteStagingLabel_MovesLabelExplicitly()
    {
        var client = new FakeSecretsManagerClient();
        client.PromoteHandler = (_, _, _, _, _) => default;
        using var resource = new SecretsManagerResource(client, "app");

        await resource.PromoteStagingLabelAsync("version-2", "AWSCURRENT", removeFromVersionId: "version-1");

        client.PromoteCalls.ShouldBe(1);
        client.LastPromoteStage.ShouldBe("AWSCURRENT");
        client.LastPromoteMoveTo.ShouldBe("version-2");
        client.LastPromoteRemoveFrom.ShouldBe("version-1");
    }

    [Test]
    public async Task Watcher_ReturnsWhenRotationMovesLabelWithoutFetchingPayload()
    {
        var client = new FakeSecretsManagerClient();
        var describes = 0;
        client.DescribeHandler = (_, _) =>
        {
            describes++;
            var current = describes < 3 ? "version-1" : "version-2";
            return ValueTaskCompat.FromResult(
                new SecretsManagerSecretDescription(
                    null,
                    "app",
                    new Dictionary<string, IReadOnlyList<string>>
                    {
                        ["version-1"] = describes < 3 ? ["AWSCURRENT"] : ["AWSPREVIOUS"],
                        ["version-2"] = describes < 3 ? ["AWSPENDING"] : ["AWSCURRENT"],
                    },
                    null
                )
            );
        };
        using var resource = new SecretsManagerResource(
            client,
            "app",
            new SecretsManagerResourceOptions { PollingInterval = TimeSpan.FromMilliseconds(10) }
        );

        resource.IsWatchSupported.ShouldBeTrue();
        await resource.WaitForChangeAsync("version-1");

        client.GetCalls.ShouldBe(0);
        describes.ShouldBeGreaterThanOrEqualTo(3);
    }

    [Test]
    public async Task Watcher_ReturnsImmediatelyWhenAlreadyRotated()
    {
        var client = new FakeSecretsManagerClient();
        client.DescribeHandler = (_, _) =>
            ValueTaskCompat.FromResult(
                new SecretsManagerSecretDescription(
                    null,
                    "app",
                    new Dictionary<string, IReadOnlyList<string>>
                    {
                        ["version-2"] = ["AWSCURRENT"],
                    },
                    null
                )
            );
        using var resource = new SecretsManagerResource(
            client,
            "app",
            new SecretsManagerResourceOptions { PollingInterval = TimeSpan.FromHours(1) }
        );

        await resource.WaitForChangeAsync("version-1");

        client.DescribeCalls.ShouldBe(1);
        client.GetCalls.ShouldBe(0);
    }

    [Test]
    public async Task Watcher_MissingOrDeletedSecretSignalsChange()
    {
        var missingClient = new FakeSecretsManagerClient();
        missingClient.DescribeHandler = (_, _) =>
            ValueTaskCompat.FromException<SecretsManagerSecretDescription>(
                new ResourceNotFoundException("Secrets Manager can't find the specified secret.")
            );
        using var missingResource = new SecretsManagerResource(missingClient, "missing");
        await missingResource.WaitForChangeAsync("version-1");

        var deletedClient = new FakeSecretsManagerClient();
        deletedClient.DescribeHandler = (_, _) =>
            ValueTaskCompat.FromResult(
                new SecretsManagerSecretDescription(
                    null,
                    "app",
                    new Dictionary<string, IReadOnlyList<string>>(),
                    DateTime.UtcNow
                )
            );
        using var deletedResource = new SecretsManagerResource(deletedClient, "deleted");
        await deletedResource.WaitForChangeAsync("version-1");
    }

    [Test]
    public async Task Watcher_DisabledWatchingHasNoWatcher()
    {
        var client = new FakeSecretsManagerClient();
        using var resource = new SecretsManagerResource(
            client,
            "app",
            new SecretsManagerResourceOptions { WatchEnabled = false }
        );

        resource.IsWatchSupported.ShouldBeFalse();
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await resource.WaitForChangeAsync("version-1")
        );
    }

    [Test]
    public async Task Watcher_SupportsCancellationWhileWaiting()
    {
        var client = new FakeSecretsManagerClient();
        client.DescribeHandler = (_, _) =>
            ValueTaskCompat.FromResult(
                new SecretsManagerSecretDescription(
                    null,
                    "app",
                    new Dictionary<string, IReadOnlyList<string>>
                    {
                        ["version-1"] = ["AWSCURRENT"],
                    },
                    null
                )
            );
        using var resource = new SecretsManagerResource(
            client,
            "app",
            new SecretsManagerResourceOptions { PollingInterval = TimeSpan.FromHours(1) }
        );
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await resource.WaitForChangeAsync("version-1", cancellation.Token)
        );
    }

    [Test]
    public async Task Dispose_WakesActiveWatcher()
    {
        var client = new FakeSecretsManagerClient();
        client.DescribeHandler = (_, _) =>
            ValueTaskCompat.FromResult(
                new SecretsManagerSecretDescription(
                    null,
                    "app",
                    new Dictionary<string, IReadOnlyList<string>>
                    {
                        ["version-1"] = ["AWSCURRENT"],
                    },
                    null
                )
            );
        var resource = new SecretsManagerResource(
            client,
            "app",
            new SecretsManagerResourceOptions { PollingInterval = TimeSpan.FromMilliseconds(10) }
        );

        var wait = resource.WaitForChangeAsync("version-1").AsTask();
        await Task.Delay(100);
        resource.Dispose();

        await WithTimeout(wait, TimeSpan.FromSeconds(5));
        await Should.ThrowAsync<ObjectDisposedException>(async () => await resource.ReadAsync());
    }

    [Test]
    public async Task CodecPipeline_DecodesSecretPayloadAndSurfacesMalformedPayloads()
    {
        var client = new FakeSecretsManagerClient();
        client.GetHandler = (_, _, _, _) =>
            ValueTaskCompat.FromResult(
                new SecretsManagerSecretValue("\"typed-value\"", null, "version-5", ["AWSCURRENT"], null, null)
            );
        var resource = new SecretsManagerResource(client, "app");
        var source = new StateSource<string>("typed", new SerializedSource<string>(resource, new JsonStateCodec<string>(), writer: (IResourceReader)resource as IResourceWriter, watcher: (IResourceReader)resource as ISourceWatcher), new StateSourceOptions<string>());

        var read = await source.Reader.ReadAsync();

        read.Status.ShouldBe(StateReadStatus.Success);
        read.Value.ShouldBe("typed-value");
        read.Revision.ShouldBe("version-5");
    }

    [Test]
    public async Task CodecPipeline_MalformedPayloadThrowsWithoutLeakingSecret()
    {
        var client = new FakeSecretsManagerClient();
        client.GetHandler = (_, _, _, _) =>
            ValueTaskCompat.FromResult(
                new SecretsManagerSecretValue(SentinelValue, null, "version-6", ["AWSCURRENT"], null, null)
            );
        var resource = new SecretsManagerResource(client, "app");
        var source = new StateSource<string>("malformed", new SerializedSource<string>(resource, new JsonStateCodec<string>(), writer: (IResourceReader)resource as IResourceWriter, watcher: (IResourceReader)resource as ISourceWatcher), new StateSourceOptions<string>());

        var failure = await Should.ThrowAsync<Exception>(async () => await source.Reader.ReadAsync());

        failure.Message.ShouldNotContain(SentinelValue);
    }

    [Test]
    public async Task Registration_ReadsThroughCodecAndExposesCapabilities()
    {
        var stub = new StubSecretsManagerClient();
        stub.GetSecretValueHandler = (_, _) =>
            Task.FromResult(
                new GetSecretValueResponse
                {
                    SecretString = """{"Label":"from-secrets-manager"}""",
                    VersionId = "12345678-1234-1234-1234-123456789011",
                    VersionStages = ["AWSCURRENT"],
                }
            );
        stub.DescribeSecretHandler = (_, _) =>
            Task.FromResult(
                new DescribeSecretResponse
                {
                    Name = "app",
                    VersionIdsToStages = new Dictionary<string, List<string>>
                    {
                        ["12345678-1234-1234-1234-123456789011"] = ["AWSCURRENT"],
                    },
                }
            );

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                {
                    sources.FromSecretsManager(
                        new SecretsManagerSourceOptions
                        {
                            Id = "secrets",
                            SecretId = "app",
                            Client = stub,
                            Codec = StateCodecBinding.Typed(
                                new JsonStateCodec<AppSettings.Fragment>()
                            ),
                        }
                    );
                })
            );
        });

        var state = context.GetState<AppSettings>();
        (await state.GetValueAsync()).Label.ShouldBe("from-secrets-manager");
        var source = context.GetRuntimeState<AppSettings>().GetDiagnostics().Sources.Single();
        source.CanWrite.ShouldBeFalse();
        source.CanWatch.ShouldBeTrue();
        source.PhysicalOrigin.ShouldBe("secretsmanager:app");
    }

    [Test]
    public async Task Registration_WritableSourceExposesWriterAndFixedVersionHasNoWatcher()
    {
        var stub = new StubSecretsManagerClient();
        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                {
                    sources.FromSecretsManager(
                        new SecretsManagerSourceOptions
                        {
                            Id = "writable",
                            SecretId = "app",
                            Client = stub,
                            Codec = StateCodecBinding.Typed(
                                new JsonStateCodec<AppSettings.Fragment>()
                            ),
                            Writable = true,
                        }
                    );
                    sources.FromSecretsManager(
                        new SecretsManagerSourceOptions
                        {
                            Id = "pinned",
                            SecretId = "app",
                            Client = stub,
                            Codec = StateCodecBinding.Typed(
                                new JsonStateCodec<AppSettings.Fragment>()
                            ),
                            ResourceOptions = new SecretsManagerResourceOptions
                            {
                                VersionId = "pinned-id",
                            },
                        }
                    );
                })
            );
        });

        var sources = context.GetRuntimeState<AppSettings>().GetDiagnostics().Sources;
        sources.Count.ShouldBe(2);
        sources.Single(source => source.Id.Value == "writable").CanWrite.ShouldBeTrue();
        sources.Single(source => source.Id.Value == "pinned").CanWatch.ShouldBeFalse();
    }

    [Test]
    public void Registration_RequiresExactlyOneClientSource()
    {
        var stub = new StubSecretsManagerClient();
        Should.Throw<ArgumentException>(() =>
        {
            using var _ = ConfiglueApp.CreateContext(builder =>
            {
                builder.Add<AppSettings>(model =>
                    model.Sources(sources =>
                    {
                        sources.FromSecretsManager(
                            new SecretsManagerSourceOptions
                            {
                                SecretId = "app",
                                Codec = StateCodecBinding.Typed(
                                    new JsonStateCodec<AppSettings.Fragment>()
                                ),
                            }
                        );
                    })
                );
            });
        });
        Should.Throw<ArgumentException>(() =>
        {
            using var _ = ConfiglueApp.CreateContext(builder =>
            {
                builder.Add<AppSettings>(model =>
                    model.Sources(sources =>
                    {
                        sources.FromSecretsManager(
                            new SecretsManagerSourceOptions
                            {
                                SecretId = "app",
                                Client = stub,
                                ClientFactory = _ => stub,
                                Codec = StateCodecBinding.Typed(
                                    new JsonStateCodec<AppSettings.Fragment>()
                                ),
                            }
                        );
                    })
                );
            });
        });
    }

    [Test]
    public void Registration_ResolvesClientFromFactoryAndRejectsNull()
    {
        var stub = new StubSecretsManagerClient();
        IAmazonSecretsManager? resolved = null;
        using (ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                {
                    sources.FromSecretsManager(
                        new SecretsManagerSourceOptions
                        {
                            SecretId = "app",
                            ClientFactory = _ =>
                            {
                                resolved = stub;
                                return stub;
                            },
                            Codec = StateCodecBinding.Typed(
                                new JsonStateCodec<AppSettings.Fragment>()
                            ),
                        }
                    );
                })
            );
        }))
        {
            resolved.ShouldBeSameAs(stub);
        }

        Should.Throw<InvalidOperationException>(() =>
        {
            using var _ = ConfiglueApp.CreateContext(builder =>
            {
                builder.Add<AppSettings>(model =>
                    model.Sources(sources =>
                    {
                        sources.FromSecretsManager(
                            new SecretsManagerSourceOptions
                            {
                                SecretId = "app",
                                ClientFactory = _ => null!,
                                Codec = StateCodecBinding.Typed(
                                    new JsonStateCodec<AppSettings.Fragment>()
                                ),
                            }
                        );
                    })
                );
            });
        });
    }

    [Test]
    public void ResourceId_IsStableAndRedacted()
    {
        var first = new SecretsManagerResource(new FakeSecretsManagerClient(), "app");
        var same = new SecretsManagerResource(new FakeSecretsManagerClient(), "app");
        var other = new SecretsManagerResource(new FakeSecretsManagerClient(), "other");
        var context = ConfiglueResourceContext.Default;

        same.GetResourceId(context).ShouldBe(first.GetResourceId(context));
        other.GetResourceId(context).ShouldNotBe(first.GetResourceId(context));
        first.GetResourceId(context).Value.ShouldNotContain(SentinelValue);
        first.GetResourceId(context).Value.StartsWith("secretsmanager:").ShouldBeTrue();
    }

    [Test]
    public void Options_RejectInvalidConfiguration()
    {
        Should.Throw<ArgumentException>(() =>
            new SecretsManagerResource(
                new FakeSecretsManagerClient(),
                "app",
                new SecretsManagerResourceOptions { VersionId = " " }
            )
        );
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new SecretsManagerResource(
                new FakeSecretsManagerClient(),
                "app",
                new SecretsManagerResourceOptions { PollingInterval = TimeSpan.Zero }
            )
        );
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new SecretsManagerResource(
                new FakeSecretsManagerClient(),
                "app",
                new SecretsManagerResourceOptions { MaxRetryAttempts = -1 }
            )
        );
    }

    private static ConfiglueResourceContext CreateContext(string subject, RouteKey? route = null)
    {
        var key = SubjectKey.From(subject);
        return new ConfiglueResourceContext(
            new TestSubject(key),
            ResourceKey.From(key),
            route ?? RouteKey.Default
        );
    }

    private static async Task WithTimeout(Task task, TimeSpan timeout)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
        if (!ReferenceEquals(completed, task))
        {
            throw new TimeoutException("The operation did not complete in time.");
        }

        await task.ConfigureAwait(false);
    }

    private sealed record TestSubject(SubjectKey Key) : IConfiglueSubject;

    private sealed class FakeSecretsManagerClient : ISecretsManagerClient
    {
        public Func<
            string,
            string?,
            string?,
            CancellationToken,
            ValueTask<SecretsManagerSecretValue>
        > GetHandler { get; set; } =
            (_, _, _, _) =>
                ValueTaskCompat.FromException<SecretsManagerSecretValue>(
                    new InvalidOperationException("The get handler is not configured.")
                );

        public Func<string, CancellationToken, ValueTask<SecretsManagerSecretDescription>> DescribeHandler { get; set; } =
            (_, _) =>
                ValueTaskCompat.FromException<SecretsManagerSecretDescription>(
                    new InvalidOperationException("The describe handler is not configured.")
                );

        public Func<
            string,
            ReadOnlyMemory<byte>,
            bool,
            IReadOnlyList<string>,
            string,
            CancellationToken,
            ValueTask<SecretsManagerPutResult>
        > PutHandler { get; set; } = (_, _, _, _, _, _) =>
            ValueTaskCompat.FromException<SecretsManagerPutResult>(
                new InvalidOperationException("The put handler is not configured.")
            );

        public Func<string, string, string, string?, CancellationToken, ValueTask> PromoteHandler { get; set; } =
            (_, _, _, _, _) => ValueTaskCompat.FromException(
                new InvalidOperationException("The promote handler is not configured.")
            );

        public int GetCalls { get; private set; }

        public int DescribeCalls { get; private set; }

        public int PutCalls { get; private set; }

        public int PromoteCalls { get; private set; }

        public string? LastSecretId { get; private set; }

        public string? LastVersionId { get; private set; }

        public string? LastVersionStage { get; private set; }

        public ReadOnlyMemory<byte> LastPutContent { get; private set; }

        public bool LastPutAsBinary { get; private set; }

        public IReadOnlyList<string> LastPutStages { get; private set; } = [];

        public List<string> RequestTokens { get; } = [];

        public string? LastPromoteStage { get; private set; }

        public string? LastPromoteMoveTo { get; private set; }

        public string? LastPromoteRemoveFrom { get; private set; }

        public ValueTask<SecretsManagerSecretValue> GetSecretValueAsync(
            string secretId,
            string? versionId,
            string? versionStage,
            CancellationToken cancellationToken
        )
        {
            GetCalls++;
            LastSecretId = secretId;
            LastVersionId = versionId;
            LastVersionStage = versionStage;
            return GetHandler(secretId, versionId, versionStage, cancellationToken);
        }

        public ValueTask<SecretsManagerSecretDescription> DescribeSecretAsync(
            string secretId,
            CancellationToken cancellationToken
        )
        {
            DescribeCalls++;
            return DescribeHandler(secretId, cancellationToken);
        }

        public ValueTask<SecretsManagerPutResult> PutSecretValueAsync(
            string secretId,
            ReadOnlyMemory<byte> content,
            bool asBinary,
            IReadOnlyList<string> versionStages,
            string clientRequestToken,
            CancellationToken cancellationToken
        )
        {
            PutCalls++;
            LastPutContent = content.ToArray();
            LastPutAsBinary = asBinary;
            LastPutStages = [.. versionStages];
            RequestTokens.Add(clientRequestToken);
            return PutHandler(secretId, content, asBinary, versionStages, clientRequestToken, cancellationToken);
        }

        public ValueTask UpdateSecretVersionStageAsync(
            string secretId,
            string versionStage,
            string moveToVersionId,
            string? removeFromVersionId,
            CancellationToken cancellationToken
        )
        {
            PromoteCalls++;
            LastPromoteStage = versionStage;
            LastPromoteMoveTo = moveToVersionId;
            LastPromoteRemoveFrom = removeFromVersionId;
            return PromoteHandler(secretId, versionStage, moveToVersionId, removeFromVersionId, cancellationToken);
        }
    }

    private sealed class StubSecretsManagerClient : IAmazonSecretsManager
    {
        public Func<GetSecretValueRequest, CancellationToken, Task<GetSecretValueResponse>> GetSecretValueHandler { get; set; } =
            (_, _) => Task.FromException<GetSecretValueResponse>(
                new ResourceNotFoundException("Secrets Manager can't find the specified secret.")
            );

        public Func<DescribeSecretRequest, CancellationToken, Task<DescribeSecretResponse>> DescribeSecretHandler { get; set; } =
            (_, _) => Task.FromException<DescribeSecretResponse>(
                new ResourceNotFoundException("Secrets Manager can't find the specified secret.")
            );

        public void Dispose() { }

        public IClientConfig Config => throw new NotImplementedException();

        public ISecretsManagerPaginatorFactory Paginators => throw new NotImplementedException();

        public ClientConfig CreateDefaultClientConfig() => throw new NotImplementedException();

        public IAmazonService CreateDefaultServiceClient(
            AWSCredentials awsCredentials,
            ClientConfig clientConfig
        ) => throw new NotImplementedException();

        public Amazon.Runtime.Endpoints.Endpoint DetermineServiceOperationEndpoint(
            AmazonWebServiceRequest request
        ) => throw new NotImplementedException();

        public Task<BatchGetSecretValueResponse> BatchGetSecretValueAsync(
            BatchGetSecretValueRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<CancelRotateSecretResponse> CancelRotateSecretAsync(
            CancelRotateSecretRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<CreateSecretResponse> CreateSecretAsync(
            CreateSecretRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<DeleteResourcePolicyResponse> DeleteResourcePolicyAsync(
            DeleteResourcePolicyRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<DeleteSecretResponse> DeleteSecretAsync(
            DeleteSecretRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<DescribeSecretResponse> DescribeSecretAsync(
            DescribeSecretRequest request,
            CancellationToken cancellationToken = default
        ) => DescribeSecretHandler(request, cancellationToken);

        public Task<GetRandomPasswordResponse> GetRandomPasswordAsync(
            GetRandomPasswordRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<GetResourcePolicyResponse> GetResourcePolicyAsync(
            GetResourcePolicyRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<GetSecretValueResponse> GetSecretValueAsync(
            GetSecretValueRequest request,
            CancellationToken cancellationToken = default
        ) => GetSecretValueHandler(request, cancellationToken);

        public Task<ListSecretsResponse> ListSecretsAsync(
            ListSecretsRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<ListSecretVersionIdsResponse> ListSecretVersionIdsAsync(
            ListSecretVersionIdsRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<PutResourcePolicyResponse> PutResourcePolicyAsync(
            PutResourcePolicyRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<PutSecretValueResponse> PutSecretValueAsync(
            PutSecretValueRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<RemoveRegionsFromReplicationResponse> RemoveRegionsFromReplicationAsync(
            RemoveRegionsFromReplicationRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<ReplicateSecretToRegionsResponse> ReplicateSecretToRegionsAsync(
            ReplicateSecretToRegionsRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<RestoreSecretResponse> RestoreSecretAsync(
            RestoreSecretRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<RotateSecretResponse> RotateSecretAsync(
            RotateSecretRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<StopReplicationToReplicaResponse> StopReplicationToReplicaAsync(
            StopReplicationToReplicaRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<TagResourceResponse> TagResourceAsync(
            TagResourceRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<UntagResourceResponse> UntagResourceAsync(
            UntagResourceRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<UpdateSecretResponse> UpdateSecretAsync(
            UpdateSecretRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<UpdateSecretVersionStageResponse> UpdateSecretVersionStageAsync(
            UpdateSecretVersionStageRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();

        public Task<ValidateResourcePolicyResponse> ValidateResourcePolicyAsync(
            ValidateResourcePolicyRequest request,
            CancellationToken cancellationToken = default
        ) => throw new NotImplementedException();
    }
}
