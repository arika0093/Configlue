using System.Collections.Concurrent;
using Configlue.Sources;
using Configlue.Testing;

namespace Configlue.Tests;

// Review follow-up for issue #310: the composite revision contract is
// stateless (encoded revision + flat vector). These tests pin the three
// points the review flagged as under-tested.
public sealed class CompositeRevisionContract310Tests
{
    [Test]
    public void Decode_ReturnsNull_ForMissingInvalidAndLegacyRevisions()
    {
        CompositeStateSource<AppSettings.Fragment>
            .DecodeComponentRevisions(null)
            .ShouldBeNull();
        CompositeStateSource<AppSettings.Fragment>
            .DecodeComponentRevisions(string.Empty)
            .ShouldBeNull();

        // Legacy / failure revisions were never produced by EncodeComponentRevisions.
        var bogus = new[]
        {
            "composite-revision",
            "revision:jp",
            "1",
            "a.b.c",
            "a.b.c.d",
            "a.b.c.d.e.f",
            "!!!.!!!.!!!.!!!.!!!",
            "....",
            ",,,",
            "###,###",
        };
        foreach (var revision in bogus)
        {
            CompositeStateSource<AppSettings.Fragment>
                .DecodeComponentRevisions(revision)
                .ShouldBeNull();
        }
    }

    [Test]
    public async Task InvalidRevision_FallsBackToFreshContextAndNullRevision()
    {
        var subject = new MutableWatchSubject("label-key", "jp")
        {
            SecondaryResource = "retry-key",
            SecondaryRegion = "us",
        };
        var labelStore = new TrackingWatchStore();
        var retryStore = new TrackingWatchStore();
        labelStore.Set(RouteKey.From("jp"), ResourceKey.From("label-key"), Fragment("japan"));
        retryStore.Set(RouteKey.From("us"), ResourceKey.From("retry-key"), FragmentWithRetry(4));
        var label = new StateSource<AppSettings.Fragment>(
            "label",
            labelStore,
            new StateSourceOptions<AppSettings.Fragment>
            {
                Watcher = labelStore,
                ResourceKeySelector = candidate =>
                    ResourceKey.From(((MutableWatchSubject)candidate).Resource),
                RouteSelector = candidate =>
                    RouteKey.From(((MutableWatchSubject)candidate).Region),
            }
        );
        var retry = new StateSource<AppSettings.Fragment>(
            "retry",
            retryStore,
            new StateSourceOptions<AppSettings.Fragment>
            {
                Watcher = retryStore,
                ResourceKeySelector = candidate =>
                    ResourceKey.From(((MutableWatchSubject)candidate).SecondaryResource),
                RouteSelector = candidate =>
                    RouteKey.From(((MutableWatchSubject)candidate).SecondaryRegion),
            }
        );
        var composite = new CompositeStateSource<AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([label, retry])
        );
        var outerContext = new ConfiglueResourceContext(
            subject,
            ResourceKey.From("outer-key"),
            RouteKey.From("outer-route")
        );

        var read = await composite.ReadAsync(outerContext);
        read.Status.ShouldBe(StateReadStatus.Success);
        read.Revision.ShouldNotBeNull();

        // Mutate routing state after the read: a conservative wait must resolve
        // fresh contexts instead of reusing the captured ones.
        subject.Resource = "changed-label";
        subject.Region = "eu";
        subject.SecondaryResource = "changed-retry";
        subject.SecondaryRegion = "ca";

        var bogusRevisions = new string?[]
        {
            null,
            string.Empty,
            "composite-revision",
            "a.b.c",
            "!!!.!!!.!!!.!!!.!!!",
        };
        foreach (var bogus in bogusRevisions)
        {
            labelStore.LastWatchContext = null;
            labelStore.LastObservedRevision = "unreached";
            retryStore.LastWatchContext = null;
            retryStore.LastObservedRevision = "unreached";

            await composite.WaitForChangeAsync(outerContext, bogus);

            labelStore.LastWatchContext!.Value.ResourceKey.ShouldBe(
                ResourceKey.From("changed-label")
            );
            labelStore.LastWatchContext.Value.Route.ShouldBe(RouteKey.From("eu"));
            labelStore.LastObservedRevision.ShouldBeNull();
            retryStore.LastWatchContext!.Value.ResourceKey.ShouldBe(
                ResourceKey.From("changed-retry")
            );
            retryStore.LastWatchContext.Value.Route.ShouldBe(RouteKey.From("ca"));
            retryStore.LastObservedRevision.ShouldBeNull();
        }
    }

    [Test]
    public async Task InvalidRevision_DefaultContext_WaitsWithNullRevision()
    {
        var firstStore = new TrackingWatchStore();
        var secondStore = new TrackingWatchStore();
        firstStore.Set(RouteKey.Default, ResourceKey.Default, Fragment("one"));
        secondStore.Set(RouteKey.Default, ResourceKey.Default, FragmentWithRetry(1));
        var first = new StateSource<AppSettings.Fragment>(
            "first",
            firstStore,
            new StateSourceOptions<AppSettings.Fragment> { Watcher = firstStore }
        );
        var second = new StateSource<AppSettings.Fragment>(
            "second",
            secondStore,
            new StateSourceOptions<AppSettings.Fragment> { Watcher = secondStore }
        );
        var composite = new CompositeStateSource<AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([first, second])
        );

        await composite.WaitForChangeAsync(
            ConfiglueResourceContext.Default,
            "legacy-revision"
        );

        firstStore.LastWatchContext.ShouldNotBeNull();
        secondStore.LastWatchContext.ShouldNotBeNull();
        firstStore.LastObservedRevision.ShouldBeNull();
        secondStore.LastObservedRevision.ShouldBeNull();
    }

    [Test]
    public async Task RevisionVector_IsFlat_EvenWhenComponentsReturnNestedVectors()
    {
        var nestedStore = new NestedVectorStore(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        var rightStore = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { Label = Optional<string?>.Present("before") }
        );
        var composite = new CompositeStateSource<AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new StateSource<AppSettings.Fragment>(
                    "nested",
                    nestedStore,
                    new StateSourceOptions<AppSettings.Fragment>
                    {
                        Writer = nestedStore,
                        Watcher = nestedStore,
                    }
                ),
                new StateSource<AppSettings.Fragment>(
                    "right",
                    rightStore,
                    new StateSourceOptions<AppSettings.Fragment>
                    {
                        Writer = rightStore,
                        Watcher = rightStore,
                    }
                ),
            ]),
            writePlan: new StateWritePlan(
                null,
                new Dictionary<string, SourceId>(StringComparer.Ordinal)
                {
                    ["RetryCount"] = SourceId.From("nested"),
                    ["Label"] = SourceId.From("right"),
                }
            )
        );

        var read = await composite.ReadAsync(ConfiglueResourceContext.Default);

        read.Status.ShouldBe(StateReadStatus.Success);
        read.Revisions.ShouldNotBeNull();
        read.Revisions!.TryGetRevision(SourceId.From("nested"), out _).ShouldBeTrue();
        read.Revisions.TryGetRevision(SourceId.From("right"), out _).ShouldBeTrue();
        // Flattening contract (#310): deeper component vectors are not propagated.
        read.Revisions.NestedRevisions.Count.ShouldBe(0);
    }

    [Test]
    public async Task NestedOnlyChange_DoesNotConflict_FlatComparisonOnly()
    {
        var nestedStore = new FlakyNestedStore(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) },
            bumpFlatRevision: false
        );
        var rightStore = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { Label = Optional<string?>.Present("before") }
        );
        var composite = new CompositeStateSource<AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new StateSource<AppSettings.Fragment>(
                    "nested",
                    nestedStore,
                    new StateSourceOptions<AppSettings.Fragment>
                    {
                        Writer = nestedStore,
                        Watcher = nestedStore,
                    }
                ),
                new StateSource<AppSettings.Fragment>(
                    "right",
                    rightStore,
                    new StateSourceOptions<AppSettings.Fragment>
                    {
                        Writer = rightStore,
                        Watcher = rightStore,
                    }
                ),
            ]),
            writePlan: new StateWritePlan(
                null,
                new Dictionary<string, SourceId>(StringComparer.Ordinal)
                {
                    ["RetryCount"] = SourceId.From("nested"),
                    ["Label"] = SourceId.From("right"),
                }
            )
        );

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources => sources.Add(composite.CreateSource("combined")))
            );
        });

        var state = context.GetRuntimeState<AppSettings>();
        // Second component read (during write preparation) reports a different
        // nested sub-revision with the same flat revision: composite ignores it
        // because deep vectors are owned by the component itself.
        await state.SaveAsync(patch =>
        {
            patch.RetryCount = 8;
            patch.Label = "nested-ignored";
        });

        (await nestedStore.InnerReadAsync()).Value!.RetryCount.Value.ShouldBe(8);
        (await rightStore.ReadAsync()).Value!.Label.Value.ShouldBe("nested-ignored");
    }

    [Test]
    public async Task FlatChange_StillConflicts_WhenNestedVectorPresent()
    {
        var nestedStore = new FlakyNestedStore(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) },
            bumpFlatRevision: true
        );
        var rightStore = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { Label = Optional<string?>.Present("before") }
        );
        var composite = new CompositeStateSource<AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new StateSource<AppSettings.Fragment>(
                    "nested",
                    nestedStore,
                    new StateSourceOptions<AppSettings.Fragment>
                    {
                        Writer = nestedStore,
                        Watcher = nestedStore,
                    }
                ),
                new StateSource<AppSettings.Fragment>(
                    "right",
                    rightStore,
                    new StateSourceOptions<AppSettings.Fragment>
                    {
                        Writer = rightStore,
                        Watcher = rightStore,
                    }
                ),
            ]),
            writePlan: new StateWritePlan(
                null,
                new Dictionary<string, SourceId>(StringComparer.Ordinal)
                {
                    ["RetryCount"] = SourceId.From("nested"),
                    ["Label"] = SourceId.From("right"),
                }
            )
        );

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources => sources.Add(composite.CreateSource("combined")))
            );
        });

        var state = context.GetRuntimeState<AppSettings>();
        await Should.ThrowAsync<StateConflictException>(async () =>
            await state.SaveAsync(patch =>
            {
                patch.RetryCount = 9;
                patch.Label = "flat-stale";
            })
        );

        nestedStore.WriteCount.ShouldBe(0);
        (await nestedStore.InnerReadAsync()).Value!.RetryCount.Value.ShouldBe(1);
        (await rightStore.ReadAsync()).Value!.Label.Value.ShouldBe("before");
    }

    [Test]
    public void EncodeRoundtrip_PreservesDelimitersAndSpecialCharacters()
    {
        var subject = new WatchSubject("subject-key");
        var observations = new List<
            CompositeStateSource<AppSettings.Fragment>.ComponentObservation
        >
        {
            new(
                SourceId.From("left.right,left/special+test=1"),
                "rev.1,2/with+special=chars 日本語",
                new ConfiglueResourceContext(
                    "model:id.with,delimiters/special",
                    subject,
                    ResourceKey.From("res.key,with/special+chars=1"),
                    RouteKey.From("route.jp,east/special")
                )
            ),
            new(
                SourceId.From("right"),
                "a/b+c=d.e,f",
                new ConfiglueResourceContext(
                    subject,
                    ResourceKey.From("other-key"),
                    RouteKey.From("us")
                )
            ),
        };

        var encoded = CompositeStateSource<AppSettings.Fragment>.EncodeComponentRevisions(
            observations
        );
        encoded.ShouldNotBeNull();

        // Base64 segments keep "." and "," unambiguous as separators.
        encoded!.Split(',').Length.ShouldBe(2);
        foreach (var entry in encoded.Split(','))
        {
            entry.Split('.').Length.ShouldBe(5);
        }

        var decoded = CompositeStateSource<AppSettings.Fragment>.DecodeComponentRevisions(
            encoded
        );
        decoded.ShouldNotBeNull();
        decoded!.Count.ShouldBe(2);
        decoded[SourceId.From("left.right,left/special+test=1")]
            .Revision.ShouldBe("rev.1,2/with+special=chars 日本語");
        decoded[SourceId.From("left.right,left/special+test=1")]
            .ResourceKey.ShouldBe(ResourceKey.From("res.key,with/special+chars=1"));
        decoded[SourceId.From("left.right,left/special+test=1")]
            .Route.ShouldBe(RouteKey.From("route.jp,east/special"));
        decoded[SourceId.From("left.right,left/special+test=1")]
            .ModelId.ShouldBe("model:id.with,delimiters/special");
        decoded[SourceId.From("right")].Revision.ShouldBe("a/b+c=d.e,f");
        decoded[SourceId.From("right")]
            .ResourceKey.ShouldBe(ResourceKey.From("other-key"));
        decoded[SourceId.From("right")].ModelId.ShouldBeNull();
    }

    [Test]
    public void EncodeRoundtrip_PreservesNullRevisionAndNullModelId()
    {
        var subject = new WatchSubject("subject-key");
        var observations = new List<
            CompositeStateSource<AppSettings.Fragment>.ComponentObservation
        >
        {
            new(
                SourceId.From("nulls"),
                null,
                new ConfiglueResourceContext(
                    subject,
                    ResourceKey.From("resource-key"),
                    RouteKey.From("jp")
                )
            ),
            // A literal "-" must not collapse to null: it is base64-encoded.
            new(
                SourceId.From("dash"),
                "-",
                new ConfiglueResourceContext(
                    "model",
                    subject,
                    ResourceKey.From("resource-key"),
                    RouteKey.From("jp")
                )
            ),
        };

        var encoded = CompositeStateSource<AppSettings.Fragment>.EncodeComponentRevisions(
            observations
        );
        encoded.ShouldNotBeNull();

        var decoded = CompositeStateSource<AppSettings.Fragment>.DecodeComponentRevisions(
            encoded
        );
        decoded.ShouldNotBeNull();
        decoded![SourceId.From("nulls")].Revision.ShouldBeNull();
        decoded[SourceId.From("nulls")].ModelId.ShouldBeNull();
        decoded[SourceId.From("dash")].Revision.ShouldBe("-");
        decoded[SourceId.From("dash")].ModelId.ShouldBe("model");
    }

    [Test]
    public void Encode_ReturnsNullForEmpty_AndStaysBounded()
    {
        CompositeStateSource<AppSettings.Fragment>
            .EncodeComponentRevisions(
                new List<CompositeStateSource<AppSettings.Fragment>.ComponentObservation>()
            )
            .ShouldBeNull();

        var subject = new WatchSubject("subject-key");
        List<
            CompositeStateSource<AppSettings.Fragment>.ComponentObservation
        > Observations(int count)
        {
            var list = new List<
                CompositeStateSource<AppSettings.Fragment>.ComponentObservation
            >(count);
            for (var index = 0; index < count; index++)
            {
                list.Add(
                    new(
                        SourceId.From($"source-{index}"),
                        $"revision-{index}",
                        new ConfiglueResourceContext(
                            subject,
                            ResourceKey.From($"resource-{index}"),
                            RouteKey.From($"route-{index}")
                        )
                    )
                );
            }

            return list;
        }

        var two = CompositeStateSource<AppSettings.Fragment>.EncodeComponentRevisions(
            Observations(2)
        )!;
        // Typical two-component revisions stay small: no fragment payload is embedded.
        two.Length.ShouldBeLessThan(1024);

        var eight = CompositeStateSource<AppSettings.Fragment>.EncodeComponentRevisions(
            Observations(8)
        )!;
        // Growth is linear in the component count (one entry per component).
        eight.Length.ShouldBeLessThan(4096);
        (eight.Length > two.Length).ShouldBeTrue();
        var decoded = CompositeStateSource<AppSettings.Fragment>.DecodeComponentRevisions(eight);
        decoded.ShouldNotBeNull();
        decoded!.Count.ShouldBe(8);
    }

    private static AppSettings.Fragment Fragment(string? label) =>
        new() { Label = Optional<string?>.Present(label) };

    private static AppSettings.Fragment FragmentWithRetry(int retryCount) =>
        new() { RetryCount = Optional<int>.Present(retryCount) };

    private sealed record WatchSubject(string Name) : IConfiglueSubject
    {
        public SubjectKey Key => SubjectKey.From(Name);
    }

    private sealed class MutableWatchSubject(string resource, string region) : IConfiglueSubject
    {
        public string Resource { get; set; } = resource;

        public string Region { get; set; } = region;

        public string SecondaryResource { get; set; } = resource;

        public string SecondaryRegion { get; set; } = region;

        public SubjectKey Key => SubjectKey.From("same-logical-subject");
    }

    private sealed class TrackingWatchStore
        : ISourceReader<AppSettings.Fragment>,
            ISourceWatcher
    {
        private readonly ConcurrentDictionary<
            (ResourceKey Key, RouteKey Route),
            StateReadResult<AppSettings.Fragment>
        > _states = new();

        public ConfiglueResourceContext? LastWatchContext { get; set; }

        public string? LastObservedRevision { get; set; }

        public void Set(RouteKey route, ResourceKey key, AppSettings.Fragment value) =>
            _states[(key, route)] = StateReadResult<AppSettings.Fragment>.Success(
                value,
                $"revision:{route.Value}"
            );

        public ValueTask<StateReadResult<AppSettings.Fragment>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTaskCompat.FromResult(
                _states.GetValueOrDefault((context.ResourceKey, context.Route)) with
                {
                    PhysicalOrigin = $"memory:{context.Route.Value}",
                }
            );
        }

        public ValueTask WaitForChangeAsync(
            ConfiglueResourceContext context,
            string? observedRevision,
            CancellationToken cancellationToken = default
        )
        {
            LastWatchContext = context;
            LastObservedRevision = observedRevision;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NestedVectorStore(AppSettings.Fragment seed)
        : ISourceReader<AppSettings.Fragment>,
            ISourceWriter<AppSettings.Fragment>,
            ISourceWatcher
    {
        private readonly InMemoryStateSource<AppSettings.Fragment> _inner = new(seed);

        public async ValueTask<StateReadResult<AppSettings.Fragment>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            var result = await _inner.ReadAsync(context, cancellationToken).ConfigureAwait(false);
            return result with
            {
                Revisions = new StateRevisionVector(
                    [new StateRevision(SourceId.From("nested"), result.Revision)],
                    [
                        new KeyValuePair<SourceId, StateRevisionVector>(
                            SourceId.From("sub"),
                            new StateRevisionVector([new StateRevision(SourceId.From("sub"), "s1")])
                        ),
                    ]
                ),
            };
        }

        public ValueTask<StateWriteResult> WriteAsync(
            ConfiglueResourceContext context,
            StateWriteRequest<AppSettings.Fragment> request,
            CancellationToken cancellationToken = default
        ) => _inner.WriteAsync(context, request, cancellationToken);

        public ValueTask WaitForChangeAsync(
            ConfiglueResourceContext context,
            string? observedRevision,
            CancellationToken cancellationToken = default
        ) => _inner.WaitForChangeAsync(context, observedRevision, cancellationToken);
    }

    private sealed class FlakyNestedStore(AppSettings.Fragment seed, bool bumpFlatRevision)
        : ISourceReader<AppSettings.Fragment>,
            ISourceWriter<AppSettings.Fragment>,
            ISourceWatcher
    {
        private readonly InMemoryStateSource<AppSettings.Fragment> _inner = new(seed);
        private readonly bool _bumpFlatRevision = bumpFlatRevision;
        private int _reads;

        public int WriteCount { get; private set; }

        public async ValueTask<StateReadResult<AppSettings.Fragment>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            var result = await _inner.ReadAsync(context, cancellationToken).ConfigureAwait(false);
            var reads = Interlocked.Increment(ref _reads);
            var flat = result.Revision;
            if (_bumpFlatRevision && reads >= 2 && flat is not null)
            {
                flat += "-external";
            }

            var subRevision = reads >= 2 ? "s2-external" : "s1";
            return result with
            {
                Revision = flat,
                Revisions = new StateRevisionVector(
                    [new StateRevision(SourceId.From("nested"), flat)],
                    [
                        new KeyValuePair<SourceId, StateRevisionVector>(
                            SourceId.From("sub"),
                            new StateRevisionVector([
                                new StateRevision(SourceId.From("sub"), subRevision),
                            ])
                        ),
                    ]
                ),
            };
        }

        public async ValueTask<StateWriteResult> WriteAsync(
            ConfiglueResourceContext context,
            StateWriteRequest<AppSettings.Fragment> request,
            CancellationToken cancellationToken = default
        )
        {
            WriteCount++;
            return await _inner
                .WriteAsync(context, request, cancellationToken)
                .ConfigureAwait(false);
        }

        public ValueTask WaitForChangeAsync(
            ConfiglueResourceContext context,
            string? observedRevision,
            CancellationToken cancellationToken = default
        ) => _inner.WaitForChangeAsync(context, observedRevision, cancellationToken);

        public ValueTask<StateReadResult<AppSettings.Fragment>> InnerReadAsync() =>
            _inner.ReadAsync();
    }
}
