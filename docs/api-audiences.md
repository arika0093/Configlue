# API ownership and dependency direction

The application workflow uses `Configlue`, `Configlue.Core`, and provider registration helpers.
`Configlue.Abstraction` contains the contracts consumed inward by Core. Core never references
`Configlue.Extensibility`. The optional Extensibility SDK references Core and adapts its controlled
internals through an explicitly named friend assembly. Official providers reference this SDK.

Provider serialization, transformed resources, and mounted source registration belong to
`Configlue.Extensibility`. Low-level resource, codec, source, transformer, and migration contracts
remain in Abstraction because Core consumes them directly. These contracts are CLR-public for
third-party providers; moving them outward would introduce a circular dependency.

`IConfiglueSourceDefinition.Create` receives a `ConfiglueSourceCreationContext`. Providers declare
resources they create using `Own`, then return `Complete(source)`. Resources supplied by an application
or DI remain borrowed. Core adopts owned resources even when creation throws, and deduplicates them
by reference in the context lifetime. A result can also explicitly supply its owned resources.

`model.Sources(sources => ...)` runs during structural registration. Runtime-dependent registration
uses `model.ConfigureSources(registration => ...)`, with `StateName`, `Services`, and `Sources`.
Adding another contextual value does not require new overload families. Runtime callbacks execute
after DI service registration and also work with null services in independent contexts.

## Public type audit

The following classification covers the public types declared in Abstraction and Core. Generic
arity is omitted; types with several arities have the same audience. Nested public members follow
their declaring type's audience. Compiler operations and transport are separated further by the
compiler ABI cleanup; capability contracts separate application inspection from administration.

| Surface | Audience | Ownership decision |
| --- | --- | --- |
| States, sessions, profiles, source selectors, validation, write plans, details | Application | Keep typed user-facing APIs; segregate capabilities |
| Resource, state, codec, source, transformer and migration contracts | Provider SPI | Minimal shared contracts stay inward in Abstraction |
| Serialization, transformation and mounted registration helpers | Provider SDK | Move outward to Extensibility |
| Generated static model contracts, schema operations, details snapshot transport | Compiler ABI | Dedicated CompilerServices surface; hide from IntelliSense |
| Builder source-build, validator/migration retrieval and runtime construction plumbing | Implementation leakage | Internalize behind the compiler runtime bridge |
| Runtime concrete state and source composition implementations | Advanced runtime | Needed for explicit low-level composition; ordinary code uses capabilities |
| File resource, locking, backups and migration journal | Built-in runtime/resource | Keep the shared physical resource implementation inward |

Public API snapshots are reviewed by assembly and, for the compiler ABI, by audience. Providers
should depend on the SDK rather than on incidental public runtime implementation methods. Generated
code uses static dispatch with closed model/fragment types so these boundaries work with Native AOT.
