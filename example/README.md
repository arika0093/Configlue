# Examples

`Example.ConsoleApp` shows a generated settings model backed by a JSON file resource. Run it from the repository root:

```sh
dotnet run --project example/Example.ConsoleApp
dotnet run --project example/Example.ConsoleApp -- --set-name Ada
```

The settings file is written beside the executable. The sample uses `IWritableOptions<T>` to read and save the generated model through the JSON state codec.

`Example.ConsoleApp.NativeAot` uses source-generated `System.Text.Json` metadata and projects the persisted model into Configlue's sparse fragment. Publish it for Linux with:

```sh
dotnet publish example/Example.ConsoleApp.NativeAot --configuration Release --runtime linux-x64 --self-contained true
```

`Example.ConsoleApp.Yaml` persists the same kind of generated model as YAML, using camel-case member names:

```sh
dotnet run --project example/Example.ConsoleApp.Yaml
dotnet run --project example/Example.ConsoleApp.Yaml -- --set-name Ada
```
