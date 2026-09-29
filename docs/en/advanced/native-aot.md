---
title: NativeAOT
description: Trim-safe configuration with source-generated serialization metadata.
---

With source-generated JSON metadata and the fragment schema, Configlue works in NativeAOT environments. Pass a `JsonSerializerContext` to the codec and, for YAML, generated serializer options with the fragment schema.

```csharp
// JSON: source-generated metadata keeps the codec trim-safe.
new JsonStateCodec<SampleSetting.Fragment>(SampleSettingJsonContext.Default.SampleSettingFragment)
```

For YAML, generate a `YamlSerializerContext` covering the model, fragment, and scalar types, then verify the round trip at startup. The `example/Example.ConsoleApp.NativeAot` project shows the full pattern: it publishes with `PublishAot=true`, projects the persisted model into Configlue's sparse fragment via `StateSourceProjection.Project`, and checks the YAML codec before creating the context:

```sh
dotnet publish example/Example.ConsoleApp.NativeAot --configuration Release --runtime linux-x64 --self-contained true
```

Prefer the `JsonTypeInfo`-based codec constructors (also used by the [legacy decoders](../migration/adopting-configuration-writable.md)) over the reflection-based overloads in trimmed applications. `JsonSchemaGenerator.Generate` likewise takes a source-generated `IJsonTypeInfoResolver` — see [JSON Schema and testing](./json-schema-and-testing.md).

`XmlStateCodec` uses `XmlSerializer` reflection and runtime code generation. Its codec methods are annotated with trimming and dynamic-code requirements, so calls from trimmed or NativeAOT applications produce analyzer warnings; there is currently no generated XML codec path.

To follow this as a guided tutorial, see [NativeAOT deployment](../getting-started/10-native-aot.md).

## Next steps

* [JSON Schema and testing](./json-schema-and-testing.md).
* [Examples](../getting-started/examples.md).
