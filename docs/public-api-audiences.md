# Configlue public API audiences

Every public type in `Configlue.Abstraction`, `Configlue.Core` (including the
`Configlue.Extensibility` snapshot), and the low-level `Configlue.State`,
`Configlue.Resources`, `Configlue.Sources`, and `Configlue.Transformers`
namespaces belongs to exactly one audience. Ordinary application code should
only need the **application** audience; everything else is hidden from ordinary
IntelliSense with `EditorBrowsable(Advanced)` (advanced opt-in) or
`EditorBrowsable(Never)` (generated/runtime plumbing that must stay CLR-public).

## Ownership model (CODEOWNERS-style note)

| Namespace | Audience | Owner mindset |
| --- | --- | --- |
| `Configlue` (model/state API, attributes, validators, states) | application + advanced-application | Application-facing. Adding an ordinary (unhidden) type here is a compatibility commitment: prefer `Advanced` unless getting-started code needs it. |
| Provider fluent namespaces (`Configlue.Provider.*`, `Configlue.Source.*`, `Configlue.Resource.*`, `Configlue.Transformer.*`, presets) | application (registration helpers) | Application-facing registration. Provider internals behind these facades stay provider SPI. |
| `Configlue.Extensibility`, `Configlue.Sources`, `Configlue.Resources`, `Configlue.State`, `Configlue.Codecs`, `Configlue.Transformers`, `Configlue.Migrations` | provider / advanced-application | Provider and advanced composition SPI. New public types here must be `Advanced` (or `Never`) and appear in the table below. |
| `Configlue.CompilerServices` | generated ABI | CLR-public only because generated code lives in consumer assemblies. New types must be `Never` (or `Advanced` for the dynamic tooling surface) and are snapshotted separately (`*.CompilerServices.approved.txt`). |

Growth rule: a pull request that adds a public type without an audience entry
here, without the required `EditorBrowsable` hiding, or in a new namespace,
must fail review. `ApiAudienceOwnershipTests` enforces the namespace and
hiding rules mechanically.

I/C/O records whether external code must **implement** the contract,
**construct** values of the type, or merely **observe** them.

## `Configlue.Abstraction` — `Configlue.Codecs`

| Type | Audience | I/C/O |
| --- | --- | --- |
| `DocumentLayout`, `DocumentLayoutOptions` | provider | C |
| `IStateCodec`, `IStateCodec<T>` | provider | I |
| `IStateCodecRecoveryPolicy`, `IStateSchemaMetadataReader` | provider | I |
| `IStateCodecWithMetadata<T>` | provider (advanced perf) | I |
| `StateCodecBinding`, `StateCodecContext`, `StateCodecDecodeResult<T>`, `StateSchemaReference` | provider | C/O |

## `Configlue.Abstraction` — `Configlue` (application surface)

Ordinary application audience (no hiding):

| Type | I/C/O |
| --- | --- |
| `ConfiglueModelAttribute`, `ConfigluePreviousVersionAttribute`, `ConfiglueEnvironmentAttribute`, `ConfiglueMergeAttribute`, `MergeMode`, `ConfiglueCloneReferenceSafeAttribute` | C (model authoring) |
| `Optional<T>`, `FragmentOperation<T>`, `FragmentOperationKind` | C (patch authoring) |
| `IReadOnlyState<T>`, `IWritableState<T>` | O (injected state boundary) |
| `IConfiglueValidator<T>`, `INamedConfiglueValidator<T>`, `ConfiglueValidationException`, `ReadValidationMode`, `WriteConflictResolution` | I/C (app validation) |
| `ConfiglueApp`, `ConfiglueBuilder`, `ConfiglueContext`, `ConfiglueModelBuilder<TModel>` (in Core) | C (composition root) |

Advanced-application audience (`Advanced`):

| Type | I/C/O |
| --- | --- |
| `IConfiglueFragment`, `IConfiglueFragment<TSelf>`, `ConfiglueFragmentMember`, `IConfigluePatch`, `IConfiglueModelPatch<TModel>`, `IConfiglueReplacementPatch`, `ConfiglueMemberExtensions` | O/C (generated contracts) |
| `ConfiglueModelSchema`, `ConfiglueMemberSchema`, `IConfiglueDeepCloneable<T>`, `IConfiglueValueCloneProvider<T>` | O/I |
| `IConfiglueSources<T>`, `ConfiglueSourceHandle<TModel>`, `StateSourcePatch`, `SourceKey<TModel>` | C (explicit source ops) |
| `IConfiglueEditSessions<T>`, `EditSession<T>`, `StateCommitResult<T>`, `ConfiglueRebaseResult`, `ConfiglueRebaseConflict`, `ConfiglueRebaseConflictKind` | C/O (draft editing) |
| `IConfiglueStateRegistry<T>`, `IConfiglueStateRegistryNotificationDeferrer<T>`, `IConfiglueStateRegistryNotificationDeferral<T>`, `ConfiglueNamedState<TModel>` (Core) | I/C (dynamic states; deferrer is implemented by custom registries) |
| `ISubjectState<T>`, `IConfiglueSubject`, `IConfiglueSubjectAccessor`, `IConfiglueSubjectAccessor<TSubject>`, `IConfiglueSubjectChangeSource` | I/C (subjects) |
| `IConfiglueProfiledState<TModel>`, `ConfiglueProfileCatalog` | C (profiles) |
| `IConfiglueDiagnostics<T>`, `IConfiglueReloadFailureDiagnostics<T>`, `IConfiglueRuntimeDiagnostics`, `ConfiglueCheckOperation`, `ConfiglueCheckResult`, `ConfiglueCheckStatus`, `ConfiglueSourceCheckResult`, `ConfiglueRuntimeDiagnosticExtensions`, `ConfiglueRuntimeDiagnosticSnapshot`, `ConfiglueRuntimeSourceSnapshot`, `ConfiglueDiagnosticEvent`, `ConfiglueDiagnosticEventKind`, `ConfiglueStateDiagnostics`, `ConfiglueSourceDiagnostics`, `ConfiglueInterfaceExtensions`, `ConfiglueTelemetry` (Core), `ConfiglueRuntimeDiagnosticOptions` (Core) | O (observability + operational checks) |
| `ConfigSourceDetails`, `ConfigSourceValueDetails<T>`, `ConfigSourceValueState`, `ConfigValueDetails<T>`, `ConfigCollectionDetails<T>`, `ConfigCollectionElementDetails<T>`, `ConfiglueEditability`, `ConfigSourceResolutionDetails` | O (details) |
| `StateWritePlan`, `StateWritePlanBuilder<TModel>`, `IConfiglueWritePreview<T>`, `StateWritePreview`, `StateStorageMigrationExtensions` (Core) | C (write ownership / migrations) |
| `StateSnapshot<T>`, `ConfiglueStateSnapshotExtensions`, `ConfiglueStateExtensions` | O (snapshots / explicit ops) |
| `StateWriteReceipt`, `StateSourceWriteResult`, `StateMultiWriteException`, `StateRevision`, `StateRevisionVector` | O (write observability) |
| `SourceId`, `SourceKey<TModel>`, `SubjectKey`, `ResourceKey`, `RouteKey` | O (routing identities stay distinct but are not ordinary vocabulary) |

Generated/runtime plumbing (`Never`, implemented/observed by generated code only):

| Type | I/C/O |
| --- | --- |
| `ConfiglueValueComparer`, `ConfiglueCollectionMerger`, `ConfiglueCollectionRebase`, `ConfiglueFragmentRebase`, `ConfiglueMergeProvenance` | — (called by generated code) |

## `Configlue.Abstraction` — provider SPI namespaces

All `Advanced` (hidden from ordinary IntelliSense).

| Namespace | Types | I/C/O |
| --- | --- | --- |
| `Configlue.Extensibility` | `IConfiglueSourceDefinition`, `ConfiglueSourceCreationContext`, `ConfiglueSourceCreation<T>`, `IConfiglueRuntimeLifetimeSource` | I (definitions); C (context/creation via runtime) |
| `Configlue.Sources` | `ISourceReader<T>`, `ISourceWriter<T>`, `ISourceWatcher`, `ISourceCapabilities<T>`, `IAsyncSourceWriteBatchParticipant<T>`, `StateSource<T>`, `StateSourceOptions<T>`, `StateSourceSet<T>`, `StateSourceProjection`, `StateFallbackCondition`, `RuntimeLifetimeRequirement`, `RuntimeLifetimeRequirementExtensions` | I (reader/writer/watcher/capabilities); C (`StateSource` via options) |
| `Configlue.Resources` | `IResourceReader`, `IResourceWriter`, `IResourceIdentity`, `ITryResourceIdentity`, `IResourceBatchWriter`, `IResourceBatchParticipant`, `IResourceBatchCompatibility`, `ResourceBatchCompatibility` (runtime helper), `IResourceBackupRecovery`, `IContextualResourceBackupRecovery`, `ConfiglueResourceContext`, `ResourceId`, `ResourceReadResult`, `ResourceWriteRequest`, `ResourceWriteMutation`, `ResourceContextExtensions`, `IConfiglueHostPaths`, `ConfiglueStandardLocation` | I (resource facets); C (results/requests/mutations); O (context flows in) |
| `Configlue.State` | `StateReadResult<T>`, `StateReadStatus`, `StateWriteRequest<T>`, `StateWriteResult`, `StateWriteBatchPlan`, `RevisionCondition`, `StateRevision`, `StateRevisionVector`, `StateSchemaMetadata`, `StateConflictException`, `IStateReaderMiddleware<T>`, `IStateWriterMiddleware<T>` | I (middleware); C/O (results) |
| `Configlue.Transformers` | `IStateByteTransformer`, `ISynchronousStateByteTransformer`, `IAsyncStateByteTransformer`, `IDestinationStateByteTransformer` (advanced perf), `IStateByteTransformerRecoveryPolicy` | I |
| `Configlue.Migrations` | `IStateSchemaMigration<T>`, `StateSchemaDispatcher<T>`, `StateSchemaMigrationChain<T>`, `IStateStorageMigrationJournal`, `IStateStorageMigrationLeaseProvider`, `StateStorageMigrationDefinition<TFragment>`, `StateStorageMigrationTarget<TFragment>`, `StateStorageMigrationProgress`, `StateStorageMigrationResult`, `StateStorageMigrationTargetResult`, `StateSourceMigrationResult` | I (migrations/journals); C (definitions) |

Performance contracts kept public with justification (`#230`, still respected):
`IStateCodecWithMetadata<T>`, `IDestinationStateByteTransformer`,
`IPipelineResourceReader`, `IPipelineStateCodec<T>`. Each is optional, documented
with buffering/lifetime requirements, and unnecessary for the canonical
`SerializedSource<T>` path.

## `Configlue.Core`

| Type | Audience | I/C/O |
| --- | --- | --- |
| `ConfiglueApp`, `ConfiglueBuilder`, `ConfiglueContext`, `ConfiglueModelBuilder<TModel>`, `ConfiglueValidationException` | application | C |
| `ConfiglueNamedState<TModel>`, `ConfiglueRuntimeDiagnosticOptions`, `ConfiglueTelemetry`, `ConfiglueSourceRegistrationContext<TModel>`, `ConfiglueSourceSetBuilder`, `ConfiglueSourceSetBuilder<TModel>`, `StateSourceSetBuilder<T>`, `StateSourceBuilder<T>`, `StateStorageMigrationExtensions` | advanced-application / provider | C |
| `ConfiglueHostPathProfile`, `ConfiglueStandardPaths`, `FileResourceOptions`, `FileBackupDirectoryMode`, `FileChangeDetectionMode`, `CommonFileSourceSettings` | provider (advanced resource) | C |
| `PipelineResourceReadResult`, `PipelineResourceReader` | provider (advanced perf) | C |
| `CompositeStateSource<TFragment>` | provider (advanced composition) | C |
| `ConfiglueStorageMigrationBuilder<TModel>` | advanced-application (migration-only definitions) | C |
| `IPipelineResourceReader`, `IPipelineStateCodec<T>` | provider (advanced perf) | I |

Core stays storage/format/host neutral: concrete file (`FileResource`) and ZIP
(`ZipEntryResource`) live in the standard `Configlue` layer (#260, #261), while
host-path contracts stay in `Abstraction` and the default profile/resolution
plumbing remains Core runtime machinery shipped to users through the primary
`Configlue` package.

## `Configlue.Extensibility` snapshot (lives in Core)

| Type | Audience | I/C/O |
| --- | --- | --- |
| `SerializedSource<T>` | provider (canonical Resource+Codec entry) | C |
| `TransformingResource` | provider (composition helper) | C |
| `IConfiglueSourceRegistrationSink`, `ConfiglueSourceRegistration`, `ConfiglueSourceSetBuilderMountExtensions` | provider (registration port) | I/C |

## Standard file composition (lives in `Configlue`)

Codecs describe representation and resources describe storage; the standard
layer owns the upper composition of the two for files, so format packages
never construct `FileResource` themselves.

| Type | Audience | I/C/O |
| --- | --- | --- |
| `FileResource` | provider (advanced resource) | C |
| `ZipEntryResource`, `ZipEntryResourceOptions` (ZIP backing for `SingleBinary`) | provider (advanced resource) | C |
| `FileSourceComposition` | provider (standard file composition entry) | C |
| `JsonFileSourceOptions`, `JsonFileSourceRegistration`, `JsonFileRegistration<TModel>`, `SingleFileJsonExtensions`, `CommonJsonFileSourceExtensions` | application (standard JSON file experience) | C |
| `FileStateStorageMigrationJournal` | advanced-application (migration stores) | C |

`CommonSourceBuilder`/`UseCommonSources`, `SingleBinaryBuilder`/`UseSingleBinary`,
environment preset builders, and file preset SPI stay standard. HTTP preset
(`CommonHttpSourceBuilder`/`WithHttpPolicy`) is an opt-in in
`Configlue.Source.Http`; CommandLine preset (`WithCommandLine`) is an opt-in in
`Configlue.Source.CommandLine`.

## `Configlue.CompilerServices` (both assemblies)
Generated ABI. CLR-public because generated code lives in consumer assemblies;
`Never` hides it from hand-written IntelliSense. The `Advanced` subgroup is the
hand-writable dynamic tooling surface (codecs, routing, diagnostics).

| Type | Hiding |
| --- | --- |
| `ConfiglueModelOperations<TModel, TFragment>`, `ConfiglueFragmentRegistry<TFragment>`, `ConfiglueModelSchemaRegistry<TModel>`, `ConfiglueModelSchemaCatalog`, `ConfiglueModelDescriptor<TModel>` (Core), `ConfiglueRuntime` (Core), `IConfiglueFacadeModel<TSelf>` (Core), `IConfiglueModel`, `IConfiglueModel<TSelf, TFragment>`, `ConfiglueReferenceEqualityComparer`, `ConfiglueWriteRouting`, `ConfiglueMemberPath`, `ConfiglueDetailsSnapshot`, `ConfigCollectionElementData`, `IConfiglueDetailsRuntime`, `ConfigluePresentMembers.Enumerator` | `Never` |
| `IConfiglueDynamicFragment`, `IConfiglueOrdinalDynamicFragment`, `IConfiglueDynamicMemberPatch`, `IConfiglueRoutablePatch`, `ConfiglueDynamicFragmentExtensions`, `ConfigluePresentMembers`, `ConfiglueFragmentMember` (in `Configlue`) | `Advanced` |

## `Configlue.DevTools` / `Configlue.DevTools.Web` (`#246`)

Development-only, optional packages outside production graphs. They introduce
no new namespaces into `Configlue.Abstraction` or `Configlue.Core`, so
`ApiAudienceOwnershipTests` stays green; the new namespaces are dev-only by
construction:

| Namespace | Audience |
| --- | --- |
| `Configlue.DevTools` | development-only (live-state projections, JSON canonical form, effective-state viewer documents/sessions) |
| `Configlue.DevTools.Web` | development-only (loopback host; BlazorMonaco viewer component plus narrow Monaco bridge; DI extension lives in `Configlue` for discoverability) |

Rules: no new public inspection API (reuse `GetDetailsAsync` transport,
diagnostics snapshots/events, `Check()`, edit sessions, schema metadata,
registries), `#244` redaction enforced in serialized payloads, and PublicApi
approvals pin the small surface (`Configlue.DevTools.approved.txt`,
`Configlue.DevTools.Web.approved.txt`).

`#247` adds viewer contracts (`ConfiglueViewer*`), the viewer session, the
`ConfiglueEffectiveStateViewer<TModel>` component, and the narrow
`ConfiglueMonacoBridge` to these existing development-only namespaces. No new
namespace is introduced, so `ApiAudienceOwnershipTests` stays green; the
official `BlazorMonaco` NuGet package is referenced only by the
development-only `Configlue.DevTools.Web` package.

`#262` replaces the fallback `TcpListener` host with the real Blazor Web App
host (`AddRazorComponents`/`AddInteractiveServerComponents`,
`MapRazorComponents<DevToolsApp>`, prerendering off) and composes the actual
UI (`DevToolsApp`, `ConfiglueDevToolsHome` at `/`, `ConfiglueDevToolsShell`
with `Editor | Diagnostics` tabs, the non-generic
`ConfiglueDevToolsEditorHost` dispatcher around
`ConfiglueEffectiveStateEditor<TModel>`, and
`ConfiglueDevToolsDiagnosticsPanel` with explicit check). The `/api/*`
surface is deleted, so no transport types are added. No new namespace is
introduced, so `ApiAudienceOwnershipTests` stays green; PublicApi approvals
pin the added component surface (`Configlue.DevTools.Web.approved.txt`).
Dispatch metadata (`ModelType`/`UntypedState`) lives on the internal entry
interface, so it is not a new public inspection API.

## What changed for `#260`

- File-source composition moved out of format providers: `FileResource`
  construction plus codec/section composition is owned by the standard
  `Configlue` layer (`FileSourceComposition.Create`), while
  `Configlue.Provider.Json/Yaml/Xml/MessagePack` keep codecs and format
  document/section semantics only.
- `FromYamlFile` / `FromXmlFile` / `FromMessagePackFile` (and
  `FromGeneratedMessagePackFile`) are thin adapters delegating to the standard
  composition; JSON file registration lives in the standard layer as part of
  the standard file experience.
- `SerializedSource<T>` stays the canonical low-level `Resource + Codec` path;
  `Configlue.Core` no longer carries concrete file storage for providers.
- Public API approvals pin the new ownership (`Configlue.Core`,
  `Configlue.Provider.Json`, `Configlue.Standard`), and
  `FileSourceOwnershipTests` forbids provider assemblies from referencing
  `FileResource` while pinning the project-reference graph.

## What changed for `#261`

- `Configlue` is the primary/default package for ordinary settings (runtime,
  JSON, file/`FileResource`, ZIP/`SingleBinary`, environment, standard paths,
  common presets, generator). It no longer references DI or HTTP, so neither is
  transitive.
- `ZipEntryResource` moved from `Configlue.Core` to the standard `Configlue`
  layer; `Configlue.Standard` approval excludes the separately snapshotted
  `Configlue.Resource.Zip` namespace.
- HTTP preset integration (`CommonHttpSourceBuilder`/`WithHttpPolicy`) moved
  from `Configlue` to opt-in `Configlue.Source.Http` (same
  `Configlue.Source.Presets` namespace, new assembly). `CommonSourceLayer.Http`
  stays for priority ordering, but the default package carries no HTTP code.
- `Configlue.Core` converges on storage/format/host-neutral runtime and
  composition machinery. Assemblies may stay finer-grained than NuGet packages;
  users receive standard impls through the top-level `Configlue` package.
- Package policy: separate NuGet package only for feature-specific third-party
  deps, narrower TFMs/platforms, host/lifecycle binding, substantial
  runtime/native cost, or clearly specialized backends.
- `PackageBoundaryTests` pins the dependency-free closure, baseline TFMs, and
  primary-package description; `verify-packages.sh` fails when `Configlue`
  depends on DI/HTTP/CommandLine/external-format/specialized packages.

## What changed for `#228`

- Physical resource plumbing (`ConfiglueResourceContext`, `ResourceId`,
  batch/mutation types, pipeline results) is provider SPI and hidden from
  ordinary completion.
- Source construction/registration ports (`StateSource<T>`, options, builders,
  registration sink) are provider/advanced and hidden from ordinary completion.
- Diagnostic implementation snapshots and merge helpers are advanced-application
  or generated plumbing, not ordinary vocabulary.
- `PipelineResourceReader.CreatePipe` (no external callers) was internalized;
  no compatibility alias was kept (pre-stable cleanup).
- `ResourceContextExtensions.TryGetResourceId(object?)` is `Never` so it no
  longer pollutes every object's completion while remaining available to
  provider wrappers.
- Routing identities (`SourceId`, `SubjectKey`, `ResourceKey`, `RouteKey`,
  `ResourceId`) stay distinct types; ordinary APIs do not require manipulating
  them.
