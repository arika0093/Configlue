---
title: "Design: Codec"
description: Bytes-to-values conversion and per-format notes.
---

# Design: Codec (conversion)

A Codec converts bytes to typed values and back, with no Resource I/O. It owns "how to read", not "where to keep".

## One Codec per format

- JSON: `JsonStateCodec`, combined with section resources, file registration, and JSON Schema export. Pass a source-generated `JsonSerializerContext` for trimming-safe, NativeAOT-friendly behavior.
- XML: the XML Codec, with section resources and file registration.
- YAML: the YAML Codec, with section resources and file registration. See `example/Example.ConsoleApp.Yaml` for camel-case naming.
- Document layouts: the JSON and YAML codecs read both the simple layout (`{ "$version": 1, ... }`, the write default) and the detailed `$configlue`/`$value` envelope. Select the write layout with `DocumentLayoutOptions` on the codec or file options; legacy `Configuration.Writable` files read as simple documents. See the [adoption guide](../migration/adopting-configuration-writable.md).

## Byte transformers and state middleware

`IStateByteTransformer` transforms persisted bytes between a Resource and a Codec. Read transforms run in registration order; write transforms run in reverse. The optional `Configlue.Transformer.AES` package provides `AesGcmStateByteTransformer` for AES-GCM encryption and authentication, and classifies authentication failures as eligible for backup recovery. Manage keys securely in the application and dispose the transformer when it is no longer needed. Pass transformers to `SerializedStateSource.FromResource`.

After the Codec, `IStateMiddleware<T>` wraps typed readers and writers for auditing, validation, normalization, and similar behavior. The first registered middleware is outermost. A middleware that wraps a writer and needs batch writes must preserve `IStateWriteBatchParticipant<T>` on its returned writer.

```csharp
using Configlue.Transformer.AES;

using var encryption = new AesGcmStateByteTransformer(key);
var source = SerializedStateSource.FromResource<AppSettings.Fragment>(
    "remote",
    resource,
    new JsonStateCodec<AppSettings.Fragment>(),
    transformers: [encryption],
    middlewares: [new AuditMiddleware()]);
```

## Choosing

When in doubt, match the file format. Add one Codec per format you read, and narrow writes to one destination (STEP 11's YAML-first, JSON-second shape is typical). Mixing formats changes nothing about Source priority or `WriteRoute`.
