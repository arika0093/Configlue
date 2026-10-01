# Package taxonomy: Extensions vs Hosting

Configlue ships optional integration packages in two families. The distinction keeps
library/adaptation integrations separate from integrations that understand the
execution host.

## `Configlue.Hosting.*` - host/platform integrations

A `Hosting.*` package understands one or more host-environment concerns, for example:

- application/runtime lifecycle
- host-specific standard paths
- current subject/user/session resolution
- host-scoped dependencies
- native/browser/platform storage
- UI/main-thread dispatch
- runtime capabilities or unsupported locations
- host-specific service registration

Current packages:

```text
Configlue.Hosting.AspNetCore
Configlue.Hosting.Blazor
```

Future host packages should follow the same convention:

```text
Configlue.Hosting.Maui
Configlue.Hosting.Unity
Configlue.Hosting.Godot
Configlue.Hosting.WindowsAppSdk
```

The name maps directly to Configlue's existing host-aware abstractions such as
`IConfiglueHostPaths`, host profiles, runtime lifetime requirements, and host
capabilities. `Hosting` is not synonymous with `Microsoft.Extensions.Hosting`; it
means the execution environment that provides paths, lifecycle, subject/runtime
scope, platform storage, or dispatch.

## `Configlue.Extensions.*` - library integrations

An `Extensions.*` package adapts Configlue to another API, library, or framework
abstraction without owning host-environment semantics. It must not need to know how
the executing host resolves paths, subjects, lifetimes, or storage.

Current packages:

```text
Configlue.Extensions.DI
Configlue.Extensions.MSOptions
Configlue.Extensions.R3
Configlue.Extensions.ComponentModel
```

Examples of library integrations:

- `Microsoft.Extensions.DependencyInjection` registration
- `Microsoft.Extensions.Options` interoperability
- R3 observables
- ComponentModel binding contracts such as `INotifyPropertyChanged` and
  `INotifyDataErrorInfo`

## Rule of thumb

Place a package under `Hosting.*` only when it understands the execution host.
Place it under `Extensions.*` when it adapts Configlue to an external library or
framework abstraction that is host-neutral.

Do not place a package under `Hosting` merely because it is commonly used by UI
applications. `Configlue.Extensions.ComponentModel`, for example, stays an extension
because it is host-neutral and is meant to be reused across WPF, WinForms, Avalonia,
MAUI, WinUI, and other UI stacks.

## Repository layout

```text
src/
  extensions/
    Configlue.Extensions.DI
    Configlue.Extensions.MSOptions
    Configlue.Extensions.R3
    Configlue.Extensions.ComponentModel

  hosting/
    Configlue.Hosting.AspNetCore
    Configlue.Hosting.Blazor
```
