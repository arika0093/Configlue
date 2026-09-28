---
title: Dapr State Management resource
description: Use a Dapr state store as an optional byte-oriented Configlue persistence resource.
---

# Dapr State Management resource

`Configlue.Resource.Dapr` adapts one Dapr State Management store key to Configlue's byte-resource contracts. Install it only when the application uses Dapr:

```sh
dotnet add package Configlue.Resource.Dapr
```

The package uses the Dapr .NET SDK and requires a configured Dapr state store and a running Dapr sidecar. It is not referenced by `Configlue.Core` or the `Configlue` meta-package.

Register a key as a typed state source and keep serialization in Configlue's codec:

```csharp
using Configlue.Provider.Json;
using Configlue.Resource.Dapr;

model.Sources(sources => sources.FromDaprState(new DaprStateSourceOptions
{
    StoreName = "state",
    Key = "project:123",
    Client = daprClient,
    Codec = new JsonStateCodec<AppSettings.Fragment>(),
}));
```

In dependency-injected applications, use `ClientFactory` to resolve the host-owned `DaprClient`. The client remains externally owned. The resource itself performs byte reads and writes; it does not implement layering, merge, provenance, schemas, migrations, or query/ORM behavior.

Reads expose the Dapr ETag as the Configlue revision when the store returns one. Conditional writes pass that expected revision to Dapr; ETag mismatches become `StateConflictException`. Dapr does not return a new ETag from a write, so successful writes report no revision until a subsequent read. ETag and concurrency guarantees depend on the selected Dapr state-store component; unsupported guarantees and component errors are surfaced rather than emulated. A zero-length value without an ETag is indistinguishable from a missing key in Dapr's byte-state response and is treated as missing.

This provider has no change watcher or cross-key transaction support. It does not promise stronger consistency or transactional behavior than the configured state store.

## Other persistence backends

Use Dapr State Management for databases and key/value stores that fit its state API; Configlue does not need a first-party adapter for each database. Object storage is a different resource shape. For example, Dapr exposes S3 through bindings, not as a State Management store; an S3 integration should therefore be a direct Configlue Resource mapping object keys, bytes, and object revisions. This package does not implement S3.
