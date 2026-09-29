---
title: Subject-scoped state
description: Serve server-wide and per-subject state from one model runtime, with independent keys and physical routes.
---

One Configlue model can serve server-wide settings and many users, tenants, or other application-defined subjects. The model keeps one static source topology; each operation supplies a subject, and each source maps that subject to its own key and physical route.

## Define a subject

Implement `IConfiglueSubject` in the application. Its `SubjectKey` is an opaque, canonical logical identity; keep placement policy such as region or residency class as separate subject metadata.

```csharp
public sealed record TenantSubject(
    string TenantId,
    string UserId,
    string Region,
    bool DataStrict) : IConfiglueSubject
{
    public SubjectKey Key =>
        SubjectKey.FromSegments("tenant", TenantId, "user", UserId);
}
```

`SubjectKey.FromSegments` encodes segments unambiguously and normalizes Unicode. Use `SubjectKey.Default` for server-wide state. Do not put database names, regions, bucket names, or other placement details into the logical key.

## Keep precedence static and map each source key

Source priority belongs to the model registration and stays the same for every subject. A source can map a subject to a different key: a user source can use the full key, a tenant source can use only the tenant segment, and a server source can always use the default key.

```csharp
model.Sources(sources =>
{
    sources.Add("server", serverReader, priority: 100)
        .KeyBy<TenantSubject>(_ => SubjectKey.Default);
    sources.Add("tenant", tenantReader, priority: 200)
        .KeyBy<TenantSubject>(subject =>
            SubjectKey.FromSegments("tenant", subject.TenantId));
    sources.Add("user", userReader, priority: 300)
        .KeyBy<TenantSubject>(subject => subject.Key);
});
```

The runtime resolves those sources in priority order for the supplied subject. If the subject-specific value is absent, the tenant and then server contribution can fill remaining fields. There is no per-user source set to create or register.

## Keep logical keys separate from physical routing

Use `model.Routing<TSubject>` to derive an opaque `RouteKey` from application metadata. The subject key still identifies the logical value; a provider resolves the route to a database, Redis endpoint, regional client, bucket, or other placement.

```csharp
model.Routing<TenantSubject>(subject =>
    subject.DataStrict
        ? RouteKey.From(subject.Region)
        : RouteKey.Default);
```

Resources receive a `ConfiglueResourceContext` containing the application subject, the source-specific `Key`, and the physical `Route`. Resource selectors can therefore choose a key and a destination independently. Resource identity includes the relevant logical key and route so writes to different physical locations do not coordinate as if they were one resource.

For example, [PostgreSQL](../reference/postgresql-resource.md) can resolve a shared `NpgsqlDataSource` per route and stores each source namespace and subject key in its own row. [Redis](../reference/redis-resource.md) can resolve a shared multiplexer per route and stores each subject row under a provider-generated hash key. [S3](../reference/s3-object-resource.md) can select an object key, bucket, and client from the resource context. [HTTP and ZIP resources](../sources/http-and-zip.md) also accept context-aware endpoint or entry selectors. A fixed file path remains global unless the application configures a separate path-aware resource.

## Use current-subject and explicit-subject views

For request or circuit scoped consumers, select an accessor on that model with `PerSubject<TAccessor>()`. The ordinary `IReadOnlyState<T>` and `IWritableState<T>` views resolve the current subject when each operation runs. Accessors are selected per model, so one model can use a tenant accessor while another remains server-wide.

```csharp
services.AddScoped<CurrentTenantAccessor>();
services.AddConfiglue(conf =>
{
    conf.Add<UserSettings>(model =>
    {
        model.PerSubject<CurrentTenantAccessor>();
        model.Sources(sources => ConfigureUserSources(sources));
    });

    conf.Add<ServerSettings>(model =>
        model.Sources(sources => ConfigureServerSources(sources)));
});
```

Implement `IConfiglueSubjectAccessor<TenantSubject>` for an application-owned accessor. For background jobs, administration, or work on a subject other than the current request, inject `ISubjectState<T>` and call `ForSubject(subject)`; the returned `IWritableState<T>` view is fixed to that subject. Both views use the same model runtime and source topology.

An accessor may implement `IConfiglueSubjectChangeSource` when its subject can change during a scope. Active change subscriptions then resolve the subject again, detach from the old watch targets, bind to the new subject and route, re-read the effective value, and notify listeners. Without change notifications, a subscription remains attached to the subject it first resolved.

## ASP.NET Core and Blazor Server

The optional `Configlue.Resource.Http.AspNetCore` package supplies adapters for HTTP request and Blazor authentication contexts. The application defines the subject type and decides which claims or services determine its key:

```csharp
services.AddHttpContextConfiglueSubjectAccessor<TenantSubject>(
    context => ResolveTenantSubject(context.User));

services.AddBlazorAuthenticationConfiglueSubjectAccessor<TenantSubject>(
    (principal, _) => ValueTask.FromResult(ResolveTenantSubject(principal)));

// On the matching model:
model.PerSubject<HttpContextConfiglueSubjectAccessor<TenantSubject>>();
// Or use BlazorAuthenticationConfiglueSubjectAccessor<TenantSubject>.
```

The HTTP adapter resolves request state for each scoped operation. The Blazor adapter observes authentication-state changes, so active `OnChange` subscriptions follow the new subject. Core has no dependency on ASP.NET Core, Blazor, or claims.

## Shared change watching

Watchers are invalidation signals: after a notification, Configlue reads authoritative state again. Resource providers share watch infrastructure by physical backend or route rather than opening a connection per subject. PostgreSQL shares a listener per data source; Redis shares a Pub/Sub subscription per multiplexer and channel. Providers without remote notifications can use a shared polling watcher. A subject change rebinds the current-subject listener to the new route.

See [application setup](../basic-usage/app-setup.md) for model registration and [reactive integration](./reactive-integration.md) for `OnChange` lifetimes.
