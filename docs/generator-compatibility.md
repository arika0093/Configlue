# Source generator compatibility policy

`Configlue.Generator` runs inside the consumer's compiler host and emits C# into the
consumer's project. Both sides of that boundary have an explicit minimum baseline so
that newer SDK/Visual Studio hosts cannot accidentally raise the requirements for
consumers such as Unity 6.

## Policy

> Configlue.Generator targets `netstandard2.0`, supports Roslyn 4.3.x or newer, and
> emits C# 9-compatible source unless a future support-policy change intentionally
> raises that baseline.

## Generator host baseline (Roslyn 4.3.x)

- The generator references `Microsoft.CodeAnalysis.CSharp` at the oldest practical
  4.3.x version (`4.3.1`) with `PrivateAssets="all"`, so the analyzer does not force a
  newer Roslyn onto consumers.
- One generator binary runs on both the oldest supported Roslyn host (Unity 6) and
  current .NET SDK / Visual Studio Roslyn hosts. The generator is intentionally not
  multi-targeted.
- Generator code must not use Roslyn APIs newer than the baseline. Where a newer
  convenience API would be required, a small compatibility implementation is preferred
  over raising the baseline (for example, `IPropertySymbol.IsRequired` is represented
  by a `RequiredMemberAttribute` symbol check in `RoslynSymbolCompat`).

## Generated source baseline (C# 9)

Generated code must compile with `LanguageVersion.CSharp9` and must not require the
consumer to enable a newer language version. In particular the generator does not emit:

- file-scoped namespaces (block namespaces are emitted instead)
- `init` accessors (generated fragments use `get; set;`)
- `[ModuleInitializer]` registration
- collection expressions (`[...]`) or other C# 10+ syntax
- LINQ or other APIs that depend on the consumer enabling `ImplicitUsings`

## Reflection-free registration without module initializers

Unity 6 does not support module initializers, so generated model registration is tied to
first use of the generated model instead of assembly load:

- generated models expose an `__Descriptor` static field whose initializer registers the
  model operations, schema, generated descriptor, and generated JSON converter;
- `ConfiglueModelOperations<TModel, TFragment>` and
  `ConfiglueModelDescriptor<TModel>` run the model type initializer with
  `RuntimeHelpers.RunClassConstructor(typeof(TModel).TypeHandle)` on first use.

This keeps registration reflection-free, assembly-scan free, deterministic, and
idempotent, and remains AOT-friendly. Future provider-specific generated registrations
(for example MessagePack) should use the same first-use registration path rather than
adding module initializers.

## Compatibility tests

`tests/Configlue.Generator.Compatibility.Tests` runs the generator on a Roslyn 4.3.1
host and compiles representative generated output with `LanguageVersion.CSharp9`. It
asserts that generated source contains no module initializer, no `init` accessor, no
`Assembly.Load`/`GetTypes` scan, and emits block namespaces. This test fails if future
generator changes introduce C# 10+ syntax or a newer Roslyn API baseline.
