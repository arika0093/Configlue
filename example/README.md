# Examples

`Example.ConsoleApp` shows a generated settings model backed by a JSON file resource. Run it from the repository root:

```sh
dotnet run --project example/Example.ConsoleApp
dotnet run --project example/Example.ConsoleApp -- --set-name Ada
```

The settings file is written beside the executable. The sample uses `IWritableOptions<T>` to read and save the generated model through the JSON state codec.
