# Resource context, routing, and source ownership

Every resource operation travels through one explicit context. There is no context-less
path that silently implies a default route.

## One operation context

`ConfiglueResourceContext` carries everything a physical operation needs:

- `ModelId` - the stable Configlue model identity
- `Subject` - the application-defined logical subject
- `Key` - the source-specific logical `SubjectKey`
- `Route` - the opaque physical placement `RouteKey`

Reads, writes, watches, identity lookup, and pipeline reads all require a context:
`IResourceReader.ReadAsync(context, ...)`, `IResourceWriter.WriteAsync(context, ...)`,
`ISourceWatcher.WaitForChangeAsync(context, ...)`, and
`IPipelineResourceReader.ReadPipelineAsync(context, ...)`. `IResourceIdentity` resolves a
`ResourceId` from a context. Context-free compatibility overloads have been removed.

## Subject-to-physical routing belongs to the source

A source is the single owner of subject-to-resource-context mapping. `KeyBy` derives the
logical `SubjectKey`; `RouteBy` derives the physical `RouteKey`. Both are separate fluent
operations because many sources need only one of them.

```csharp
sources
    .FromPostgreSql(...)
    .KeyBy<TenantSubject>(x => SubjectKey.FromSegments(x.TenantId))
    .RouteBy<TenantSubject>(x => RouteKey.From(x.Region));

sources
    .FromRedis(...)
    .KeyBy<TenantSubject>(x => SubjectKey.FromSegments(x.TenantId, x.UserId))
    .RouteBy<TenantSubject>(x => RouteKey.From(x.RedisCluster));
```

Different sources may use different key and route policies for the same model.
`StateSource.GetResourceContext(subject)` builds the complete context (model ID, original
subject, source-specific key, source-specific route) without the runtime supplying a
separate route argument. Model-level routing has been removed.

This yields a strict separation:

- source priority orders read resolution
- the write plan owns which logical source receives a write
- `KeyBy` / `RouteBy` decide where that logical source addresses the subject physically

## Model-aware physical identity

Backend resources address state by model ID plus resource namespace plus subject key, so
two Configlue models that share a namespace and subject key cannot collide. PostgreSQL
stores and notifies by `(model_id, namespace, subject_key)`. Redis derives its physical key,
notification identity, and computed `ResourceId` from the same tuple. Cross-model isolation
is covered by equivalent tests for both backends.

## Async-capable ownership

Provider-created resources may implement either `IDisposable` or `IAsyncDisposable`.
Ownership does not require an artificial synchronous wrapper. Asynchronous context disposal
awaits async-owned resources; synchronous `Dispose()` remains a blocking boundary when
necessary. The same ownership model applies to source creation and profile catalog
factories.

## Bounded subject-resolution caches

`StateSourceResolver<T>` keeps its per-subject resolution cache but bounds it. Entries track
last access and the number of active watch references. An entry is evicted only when it has
been idle for the configured timeout and no active watcher still depends on it. Eviction is
opportunistic on access - there is no timer or task per subject - and can also be triggered
by a high entry count. The default idle timeout is five minutes; unbounded growth is not the
contract.
