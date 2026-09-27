# Examples

`Example.ConsoleApp` shows a generated settings model backed by a JSON file resource and registered through the Configlue facade context. Run it from the repository root:

```sh
dotnet run --project example/Example.ConsoleApp
dotnet run --project example/Example.ConsoleApp -- --set-name Ada
```

The settings file is written beside the executable. The sample uses `IWritableOptions<T>` to read and save the generated model through the JSON state codec. It does not need a dependency injection container.

`Example.SimpleApp` shows the same file-backed workflow without a dependency injection container. It constructs `ConfiglueOptions<TModel, TFragment>` directly:

```sh
dotnet run --project example/Example.SimpleApp
dotnet run --project example/Example.SimpleApp -- --set-name Ada
```

`Example.WorkerService` registers Configlue with the Generic Host and runs a background worker. The worker reads the settings, increments `RunCount`, and saves them every five seconds. Press Ctrl+C to stop it:

```sh
dotnet run --project example/Example.WorkerService
```

`Example.MultiSource` combines a high-priority HTTP policy source with writable explicit settings and read-only local and global JSON files. Missing or temporarily unavailable HTTP policy falls through to the file layers; authorization and other permanent HTTP errors surface to the application. Save a name into the explicit file with:

```sh
dotnet run --project example/Example.MultiSource
dotnet run --project example/Example.MultiSource -- --set-name Ada
```

Set `CONFIGLUE_POLICY_URL` to the HTTP resource root to add the remote policy source. The sample reads the serialized sparse fragment from `{root}/get`. `example/Example.MultiSource/policy.json` is a small fixture for serving locally.

`Example.ConsoleApp.NativeAot` uses source-generated `System.Text.Json` metadata with fluent JSON file registration. It reads and writes a root settings file and a separate mounted database file. Publish it for Linux with:

```sh
dotnet publish example/Example.ConsoleApp.NativeAot --configuration Release --runtime linux-x64 --self-contained true
```

Pass `--set-database-host db.example.test` to verify a mounted subtree write, or `--set-name Ada` to write the root settings file.

`Example.ConsoleApp.Yaml` persists the same kind of generated model as YAML, using camel-case member names:

```sh
dotnet run --project example/Example.ConsoleApp.Yaml
dotnet run --project example/Example.ConsoleApp.Yaml -- --set-name Ada
```
