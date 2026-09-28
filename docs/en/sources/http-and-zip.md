---
title: HTTP and ZIP
description: Remote policy sources over HTTP and archive entries as resources.
---

## HTTP resources

`HttpResourceReader` reads from `{root}/get` and can be composed with any state codec. HTTP requests use ETags for conditional writes and polling (every 5 seconds by default).

```csharp
using Configlue.Provider.Json;
using Configlue.Resource.Http;

var reader = new HttpResourceReader(httpClient, new Uri("https://config.example/api/settings/"));
model.Sources(sources => sources.FromHttp(new HttpSourceOptions
{
    Id = "policy",
    EndPoint = "https://config.example/api/settings/",
    Client = httpClient,
    Codec = new JsonStateCodec<AppSettings.Fragment>(),
    Priority = 400,
}));
```

HTTP writes are disabled unless `Writable = true`; provide a codec explicitly. In DI, pass `ClientFactory = provider => provider!.GetRequiredService<IHttpClientFactory>().CreateClient("settings")` instead of a direct client so the factory owns the handler lifetime. Outside DI the supplied client stays caller-owned.

```csharp
sources.FromHttp(new HttpSourceOptions
{
    Id = "remote",
    EndPoint = "https://config.example/api/settings/",
    ClientFactory = provider => provider!.GetRequiredService<IHttpClientFactory>().CreateClient("settings"),
    Codec = new JsonStateCodec<AppSettings.Fragment>(),
    Priority = 400,
    Writable = true,
});
```

Only pass `writer: reader.CreateWriter()` to `SerializedStateSource.FromResource` when the endpoint supports updates. The optional `Configlue.Resource.Http.AspNetCore` package maps the same protocol over user-provided resource handlers — see the [HTTP resource protocol](../reference/http-resource-protocol.md). Reads that miss (`404`) or are temporarily unavailable map to fallback/not-found semantics; permanent errors surface to the application.

Host applications can register named clients with the standard `AddHttpClient` APIs and pass them to facade sources through `FromHttpClientFactory`. For JSON endpoints, `FromJsonHttp` and `FromJsonHttpClientFactory` create the JSON codec for you; set `Writable = true` only when the endpoint supports updates. These sources are read-only by default. The source resolves its client when the Configlue context is created; `IHttpClientFactory` manages the underlying handlers, and the source does not dispose the returned client. Use `FromHttp` when an endpoint uses a codec other than JSON.

## ZIP entries

`ZipEntryResource` exposes one archive entry as a logical resource while retaining the archive's physical identity and revision. Disjoint entry updates can share one batched archive write or safely rebase over a concurrent change to another entry. A concurrent change to the same entry remains a `StateConflictException`, and untouched entries remain intact.

When the archive resource implements `IStateWatcher` (for example, `FileResource`), entry change monitoring delegates to that watcher and reacts to archive file edits. Only archive resources without a watcher use revision polling, every 250 ms by default; pass a `pollingInterval` to `ZipEntryResource` to configure that fallback.

## One binary file for multiple models

`UseSingleBinary` stores each model and named options instance in its own JSON entry in one local ZIP archive. Pass a stable `storageKey` when a model's persisted identity must survive CLR type renames.

```csharp
using Configlue.Source.Presets;

await using var context = ConfiglueApp.CreateContext(config =>
    config.UseSingleBinary(binary =>
    {
        binary.WithLocal("savedata.bin")
            .WithPassphrase("a secret passphrase")
            .WithProfiles();
        binary.Add<AppSettings>(storageKey: "app");
        binary.Add<GameSettings>(storageKey: "game");
    }));
```

`WithAesKey` accepts a 16-, 24-, or 32-byte AES key; keep its memory unchanged while the registration can create contexts or named profiles. `WithEncryption` accepts a caller-owned `IStateByteTransformer`; keep it alive for the context lifetime. `WithPassphrase` (also available as `WithEncrypted(string)`) uses AES-256-GCM with PBKDF2-HMAC-SHA-256 and stores the random salt in the encrypted file. Keep the passphrase or key outside the save file.

`WithProfiles` stores a separate profile catalog for each model. Profile state, ordinary named options, and the unnamed default state use separate ZIP entries. Concurrent writes to different entries are merged; writes based on stale data for the same entry fail rather than silently overwrite one another.

## Next steps

* [Fallback and custom sources](./fallback-and-custom.md).
* [HTTP resource protocol](../reference/http-resource-protocol.md) for the wire details.
