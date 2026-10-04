using Configlue.Provider.Json;
using Configlue.Sources;
using Configlue.Testing;

namespace Configlue.Tests;

public sealed class CompositeMultiComponentWrite252Tests
{
    [Test]
    public async Task TwoComponents_NoChange_PatchToBoth_Succeeds()
    {
        var leftStore = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        var rightStore = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { Label = Optional<string?>.Present("before") }
        );
        var composite = CreateTwoComponentComposite(leftStore, rightStore);

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources => sources.Add(composite.CreateSource("combined")))
            );
        });

        var state = context.GetRuntimeState<AppSettings>();
        await state.SaveAsync(patch =>
        {
            patch.RetryCount = 2;
            patch.Label = "after";
        });

        (await leftStore.ReadAsync()).Value!.RetryCount.Value.ShouldBe(2);
        (await rightStore.ReadAsync()).Value!.Label.Value.ShouldBe("after");
    }

    [Test]
    public async Task TwoComponents_OneChangedAfterBaseline_ConflictsBeforeAnyWrite()
    {
        var leftSeed = new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) };
        var rightSeed = new AppSettings.Fragment { Label = Optional<string?>.Present("before") };
        var leftStore = new BumpRevisionStore(leftSeed);
        var rightStore = new InMemoryStateSource<AppSettings.Fragment>(rightSeed);
        var composite = new CompositeStateSource<AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new StateSource<AppSettings.Fragment>("left", leftStore, new StateSourceOptions<AppSettings.Fragment> { Writer = leftStore, Watcher = leftStore }),
                new StateSource<AppSettings.Fragment>("right", rightStore, new StateSourceOptions<AppSettings.Fragment> { Writer = rightStore, Watcher = rightStore }),
            ]),
            writePlan: new StateWritePlan(
                null,
                new Dictionary<string, SourceId>(StringComparer.Ordinal)
                {
                    ["RetryCount"] = SourceId.From("left"),
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
        var conflict = await Should.ThrowAsync<StateConflictException>(async () =>
            await state.SaveAsync(patch =>
            {
                patch.RetryCount = 99;
                patch.Label = "patched";
            })
        );
        conflict.Message.ShouldContain("changed while the patch batch was being prepared");

        leftStore.WriteCount.ShouldBe(0);
        (await leftStore.InnerReadAsync()).Value!.RetryCount.Value.ShouldBe(1);
        (await rightStore.ReadAsync()).Value!.Label.Value.ShouldBe("before");
    }

    [Test]
    public async Task SingleComponentRoutedPatch_InMultiComponentComposite_Succeeds()
    {
        var leftStore = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        var rightStore = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { Label = Optional<string?>.Present("before") }
        );
        var composite = CreateTwoComponentComposite(leftStore, rightStore);

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources => sources.Add(composite.CreateSource("combined")))
            );
        });

        var state = context.GetRuntimeState<AppSettings>();
        await state.SaveAsync(patch =>
        {
            patch.RetryCount = 5;
        });

        (await leftStore.ReadAsync()).Value!.RetryCount.Value.ShouldBe(5);
        (await rightStore.ReadAsync()).Value!.Label.Value.ShouldBe("before");
    }

    [Test]
    public async Task NestedLongestPrefix_RoutesAcrossTwoComponents()
    {
        var databaseStore = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment
            {
                Database = Optional<DatabaseSettings.Fragment?>.Present(
                    new DatabaseSettings.Fragment { Host = Optional<string>.Present("db.local") }
                ),
            }
        );
        var portStore = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment
            {
                Database = Optional<DatabaseSettings.Fragment?>.Present(
                    new DatabaseSettings.Fragment { Port = Optional<int>.Present(1111) }
                ),
            }
        );
        var composite = new CompositeStateSource<AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new StateSource<AppSettings.Fragment>("database", databaseStore, new StateSourceOptions<AppSettings.Fragment> { Writer = databaseStore, Watcher = databaseStore }),
                new StateSource<AppSettings.Fragment>("port", portStore, new StateSourceOptions<AppSettings.Fragment> { Writer = portStore, Watcher = portStore }),
            ]),
            writePlan: new StateWritePlan(
                null,
                new Dictionary<string, SourceId>(StringComparer.Ordinal)
                {
                    ["Database"] = SourceId.From("database"),
                    ["Database.Port"] = SourceId.From("port"),
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
        await state.SaveAsync(patch =>
        {
            patch.Database = new DatabaseSettings.Patch
            {
                Host = FragmentOperation<string>.Set("edited.db"),
                Port = FragmentOperation<int>.Set(9999),
            };
        });

        var resolved = await state.GetValueAsync();
        resolved.Database!.Host.ShouldBe("edited.db");
        resolved.Database.Port.ShouldBe(9999);
        (await databaseStore.ReadAsync()).Value!.Database.Value!.Host.Value.ShouldBe("edited.db");
        (await portStore.ReadAsync()).Value!.Database.Value!.Port.Value.ShouldBe(9999);
    }

    [Test]
    public async Task EditSessionCommit_FragmentChangesPatch_SucceedsAcrossTwoComponents()
    {
        var leftStore = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        var rightStore = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { Label = Optional<string?>.Present("before") }
        );
        var composite = CreateTwoComponentComposite(leftStore, rightStore);

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources => sources.Add(composite.CreateSource("combined")))
            );
        });

        var state = context.GetRuntimeState<AppSettings>();
        using var edit = await state.OpenEditSessionAsync();
        edit.Value.RetryCount = 7;
        edit.Value.Label = "edited";
        await edit.CommitAsync();

        (await leftStore.ReadAsync()).Value!.RetryCount.Value.ShouldBe(7);
        (await rightStore.ReadAsync()).Value!.Label.Value.ShouldBe("edited");
    }

    [Test]
    public async Task GeneratedRoutablePatch_DirectApply_SucceedsAcrossTwoComponents()
    {
        var leftStore = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        var rightStore = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { Label = Optional<string?>.Present("before") }
        );
        var composite = CreateTwoComponentComposite(leftStore, rightStore);

        await using var runtime = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([composite.CreateSource("combined")])
        );

        await runtime.ApplyPatchesAsync([
            new StateSourcePatch(
                SourceId.From("combined"),
                new AppSettings.Patch
                {
                    RetryCount = FragmentOperation<int>.Set(11),
                    Label = FragmentOperation<string?>.Set("routed"),
                }
            ),
        ]);

        (await leftStore.ReadAsync()).Value!.RetryCount.Value.ShouldBe(11);
        (await rightStore.ReadAsync()).Value!.Label.Value.ShouldBe("routed");
    }

    [Test]
    public async Task RouteOnlyOwnership_TwoComponents_Succeeds()
    {
        var leftStore = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        var rightStore = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { Label = Optional<string?>.Present("before") }
        );
        var composite = CreateTwoComponentComposite(leftStore, rightStore);

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources => sources.Add(composite.CreateSource("combined")))
            );
        });

        var state = context.GetRuntimeState<AppSettings>();
        using var edit = await state.OpenEditSessionAsync();
        edit.Value.RetryCount = 3;
        edit.Value.Label = "route-only";
        await edit.CommitAsync();

        (await leftStore.ReadAsync()).Value!.RetryCount.Value.ShouldBe(3);
        (await rightStore.ReadAsync()).Value!.Label.Value.ShouldBe("route-only");
    }

    [Test]
    public async Task DefaultRouteOwnership_TwoComponents_Succeeds()
    {
        var defaultStore = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        var routedStore = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { Label = Optional<string?>.Present("before") }
        );
        var composite = new CompositeStateSource<AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new StateSource<AppSettings.Fragment>("default", defaultStore, new StateSourceOptions<AppSettings.Fragment> { Writer = defaultStore, Watcher = defaultStore }),
                new StateSource<AppSettings.Fragment>("routed", routedStore, new StateSourceOptions<AppSettings.Fragment> { Writer = routedStore, Watcher = routedStore }),
            ]),
            writePlan: new StateWritePlan(
                SourceId.From("default"),
                new Dictionary<string, SourceId>(StringComparer.Ordinal)
                {
                    ["Label"] = SourceId.From("routed"),
                }
            )
        );

        composite.DefaultWriteSourceId.ShouldBe(SourceId.From("default"));

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources => sources.Add(composite.CreateSource("combined")))
            );
        });

        var state = context.GetRuntimeState<AppSettings>();
        await state.SaveAsync(patch =>
        {
            patch.RetryCount = 4;
            patch.Label = "default-routed";
        });

        (await defaultStore.ReadAsync()).Value!.RetryCount.Value.ShouldBe(4);
        (await routedStore.ReadAsync()).Value!.Label.Value.ShouldBe("default-routed");
    }

    [Test]
    public async Task RouteOnlyComposite_UnroutedMemberFailsWithoutChangingComponents()
    {
        var leftStore = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        var rightStore = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { Label = Optional<string?>.Present("before") }
        );
        var composite = CreateTwoComponentComposite(leftStore, rightStore);

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources => sources.Add(composite.CreateSource("combined")))
            );
        });

        var state = context.GetRuntimeState<AppSettings>();
        using var edit = await state.OpenEditSessionAsync();
        edit.Value.Enabled = !edit.Value.Enabled;

        var error = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await edit.CommitAsync()
        );
        error.Message.ShouldContain("no configured write owner");

        (await leftStore.ReadAsync()).Value!.RetryCount.Value.ShouldBe(1);
        (await rightStore.ReadAsync()).Value!.Label.Value.ShouldBe("before");
    }

    [Test]
    public async Task IndependentComponents_KeepAtomicity_TwoPhysicalWrites()
    {
        var leftStore = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        var rightStore = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { Label = Optional<string?>.Present("before") }
        );
        var composite = CreateTwoComponentComposite(leftStore, rightStore);

        await using var runtime = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([composite.CreateSource("combined")])
        );

        var receipt = await runtime.ApplyPatchesAsync([
            new StateSourcePatch(
                SourceId.From("combined"),
                new AppSettings.Patch
                {
                    RetryCount = FragmentOperation<int>.Set(21),
                    Label = FragmentOperation<string?>.Set("atomic"),
                }
            ),
        ]);

        receipt.PhysicalWriteCount.ShouldBe(2);
        (await leftStore.ReadAsync()).Value!.RetryCount.Value.ShouldBe(21);
        (await rightStore.ReadAsync()).Value!.Label.Value.ShouldBe("atomic");
    }

    [Test]
    public async Task NestedComponentVector_PreservesNestedSemantics()
    {
        var nestedStore = new NestedRevisionStore(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        var rightStore = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { Label = Optional<string?>.Present("before") }
        );
        var composite = new CompositeStateSource<AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new StateSource<AppSettings.Fragment>("nested", nestedStore, new StateSourceOptions<AppSettings.Fragment> { Writer = nestedStore, Watcher = nestedStore }),
                new StateSource<AppSettings.Fragment>("right", rightStore, new StateSourceOptions<AppSettings.Fragment> { Writer = rightStore, Watcher = rightStore }),
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
        await state.SaveAsync(patch =>
        {
            patch.RetryCount = 8;
            patch.Label = "nested-ok";
        });

        (await nestedStore.InnerReadAsync()).Value!.RetryCount.Value.ShouldBe(8);
        (await rightStore.ReadAsync()).Value!.Label.Value.ShouldBe("nested-ok");
    }

    [Test]
    public async Task NestedComponentVector_StaleNested_ConflictsBeforeAnyWrite()
    {
        var nestedStore = new StaleNestedRevisionStore(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        var rightStore = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { Label = Optional<string?>.Present("before") }
        );
        var composite = new CompositeStateSource<AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new StateSource<AppSettings.Fragment>("nested", nestedStore, new StateSourceOptions<AppSettings.Fragment> { Writer = nestedStore, Watcher = nestedStore }),
                new StateSource<AppSettings.Fragment>("right", rightStore, new StateSourceOptions<AppSettings.Fragment> { Writer = rightStore, Watcher = rightStore }),
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
                patch.Label = "nested-stale";
            })
        );

        nestedStore.WriteCount.ShouldBe(0);
        (await nestedStore.InnerReadAsync()).Value!.RetryCount.Value.ShouldBe(1);
        (await rightStore.ReadAsync()).Value!.Label.Value.ShouldBe("before");
    }

    [Test]
    public async Task UnavailableComponent_FailsWithoutPartialWrite()
    {
        var leftStore = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        var rightStore = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { Label = Optional<string?>.Present("before") }
        );
        var composite = CreateTwoComponentComposite(leftStore, rightStore);

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources => sources.Add(composite.CreateSource("combined")))
            );
        });

        rightStore.SetUnavailable();

        var state = context.GetRuntimeState<AppSettings>();
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await state.SaveAsync(patch =>
            {
                patch.RetryCount = 6;
                patch.Label = "unavailable";
            })
        );

        (await leftStore.ReadAsync()).Value!.RetryCount.Value.ShouldBe(1);
    }

    [Test]
    public async Task BatchCompatibleSameResource_Components_BatchOnce()
    {
        var resource = new InMemoryResource();
        var codec = new JsonStateCodec<AppSettings.Fragment>();
        IResourceReader firstSection = new JsonSectionResource(resource, "App:First");
        var firstSource = new StateSource<AppSettings.Fragment>(
            "first",
            new SerializedSource<AppSettings.Fragment>(
                firstSection,
                codec,
                writer: firstSection as IResourceWriter,
                watcher: firstSection as ISourceWatcher
            ),
            new StateSourceOptions<AppSettings.Fragment> { Priority = 10 }
        );
        IResourceReader secondSection = new JsonSectionResource(resource, "App:Second");
        var secondSource = new StateSource<AppSettings.Fragment>(
            "second",
            new SerializedSource<AppSettings.Fragment>(
                secondSection,
                codec,
                writer: secondSection as IResourceWriter,
                watcher: secondSection as ISourceWatcher
            ),
            new StateSourceOptions<AppSettings.Fragment> { Priority = 0 }
        );
        var composite = new CompositeStateSource<AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([firstSource, secondSource]),
            writePlan: new StateWritePlan(
                null,
                new Dictionary<string, SourceId>(StringComparer.Ordinal)
                {
                    ["RetryCount"] = SourceId.From("first"),
                    ["Label"] = SourceId.From("second"),
                }
            )
        );

        await using var runtime = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([composite.CreateSource("combined")])
        );

        var receipt = await runtime.ApplyPatchesAsync([
            new StateSourcePatch(
                SourceId.From("combined"),
                new AppSettings.Patch
                {
                    RetryCount = FragmentOperation<int>.Set(7),
                    Label = FragmentOperation<string?>.Set("batched"),
                }
            ),
        ]);

        receipt.PhysicalWriteCount.ShouldBe(1);
        resource.WriteCount.ShouldBe(1);
        (await firstSource.Reader.ReadAsync()).Value!.RetryCount.Value.ShouldBe(7);
        (await secondSource.Reader.ReadAsync()).Value!.Label.Value.ShouldBe("batched");
    }

    private static CompositeStateSource<AppSettings.Fragment> CreateTwoComponentComposite(
        InMemoryStateSource<AppSettings.Fragment> leftStore,
        InMemoryStateSource<AppSettings.Fragment> rightStore
    ) =>
        new(
            new StateSourceSet<AppSettings.Fragment>([
                new StateSource<AppSettings.Fragment>("left", leftStore, new StateSourceOptions<AppSettings.Fragment> { Writer = leftStore, Watcher = leftStore }),
                new StateSource<AppSettings.Fragment>("right", rightStore, new StateSourceOptions<AppSettings.Fragment> { Writer = rightStore, Watcher = rightStore }),
            ]),
            writePlan: new StateWritePlan(
                null,
                new Dictionary<string, SourceId>(StringComparer.Ordinal)
                {
                    ["RetryCount"] = SourceId.From("left"),
                    ["Label"] = SourceId.From("right"),
                }
            )
        );

    private sealed class BumpRevisionStore(AppSettings.Fragment seed)
        : ISourceReader<AppSettings.Fragment>,
            ISourceWriter<AppSettings.Fragment>,
            ISourceWatcher
    {
        private readonly InMemoryStateSource<AppSettings.Fragment> _inner = new(seed);
        private int _reads;

        public int WriteCount { get; private set; }

        public async ValueTask<StateReadResult<AppSettings.Fragment>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            var result = await _inner.ReadAsync(context, cancellationToken).ConfigureAwait(false);
            _reads++;
            if (_reads >= 2)
            {
                return result with { Revision = (result.Revision ?? "0") + "-external" };
            }

            return result;
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

    private sealed class NestedRevisionStore(AppSettings.Fragment seed)
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

        public ValueTask<StateReadResult<AppSettings.Fragment>> InnerReadAsync() =>
            _inner.ReadAsync();
    }

    private sealed class StaleNestedRevisionStore(AppSettings.Fragment seed)
        : ISourceReader<AppSettings.Fragment>,
            ISourceWriter<AppSettings.Fragment>,
            ISourceWatcher
    {
        private readonly InMemoryStateSource<AppSettings.Fragment> _inner = new(seed);
        private int _reads;

        public int WriteCount { get; private set; }

        public async ValueTask<StateReadResult<AppSettings.Fragment>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            var result = await _inner.ReadAsync(context, cancellationToken).ConfigureAwait(false);
            _reads++;
            var subRevision = _reads >= 2 ? "s2-external" : "s1";
            return result with
            {
                Revisions = new StateRevisionVector(
                    [new StateRevision(SourceId.From("nested"), result.Revision)],
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
