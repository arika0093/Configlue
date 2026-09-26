# Configlue

Configlue is a source-generator-first library for reading, resolving, editing, and persisting typed configuration from independent sources.

The repository is being rebuilt around backend-neutral state and sparse generated fragments. See [the architecture notes](docs/architecture.md) for the design direction and current implementation status.

## Projects

- `Configlue`: user-facing package with the runtime, dependency injection, and source generator.
- `Configlue.Abstraction`: provider, codec, resource, and generated-model contracts.
- `Configlue.Core`: state resolution, persistence, and dependency injection runtime.
- `Configlue.Generator`: generated sparse model support.
- `Configlue.Testing`: in-memory resources and test doubles.
- `Configlue.Provider.*`: JSON, XML, and YAML codecs.

## Build

Requires the .NET 10 SDK.

```sh
dotnet build Configlue.slnx
```
