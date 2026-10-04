using Configlue;
using Configlue.Extensibility;
using Configlue.Extensions.ComponentModel;
using Configlue.Hosting.Godot;
using Configlue.Provider.Json;
using Configlue.Sources;
using G = global::Godot;

public partial class SmokeRoot : G.Node
{
    public override async void _Ready()
    {
        var exitCode = 1;
        string? temporaryDirectory = null;
        try
        {
            var builder = new ConfiglueBuilder().UseGodot();
            var root = builder.ResolveStandardDirectory(
                ConfiglueStandardLocation.UserGlobal,
                "ignored-app-id"
            );
            Require(
                root == Path.GetFullPath(G.ProjectSettings.GlobalizePath("user://")),
                "user data root"
            );
            Require(
                builder.ResolveStandardDirectory(ConfiglueStandardLocation.BackupRoot)
                    == Path.Combine(root, "Configlue", "Backups"),
                "backup root"
            );
            foreach (
                var location in new[]
                {
                    ConfiglueStandardLocation.HostGlobal,
                    ConfiglueStandardLocation.Local,
                }
            )
            {
                try
                {
                    builder.ResolveStandardDirectory(location, "app");
                    throw new InvalidOperationException("Unsupported location resolved.");
                }
                catch (NotSupportedException) { }
            }

            temporaryDirectory = Path.Combine(
                root,
                "configlue-smoke-" + Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(temporaryDirectory);
            using var resource = new FileResource(
                Path.Combine(temporaryDirectory, "settings.json"),
                new FileResourceOptions { LockDirectory = temporaryDirectory },
                null,
                builder.HostPaths
            );
            var serialized = new SerializedSource<SmokeSettings.Fragment>(
                resource,
                new JsonStateCodec<SmokeSettings.Fragment>(),
                writer: resource,
                watcher: resource
            );
            var source = new StateSource<SmokeSettings.Fragment>(
                "file",
                serialized,
                new StateSourceOptions<SmokeSettings.Fragment>()
            );
            var written = await source.WriteAsync(
                ConfiglueResourceContext.Default,
                new StateWriteRequest<SmokeSettings.Fragment>(
                    new() { Counter = 7 },
                    RevisionCondition.MustNotExist
                )
            );
            await using var context = ConfiglueApp.CreateContext(registration =>
            {
                registration.UseGodot();
                registration.Add<SmokeSettings>(model =>
                    model.ConfigureSources(sources => sources.Sources.Add(source))
                );
            });
            var state = context.GetState<SmokeSettings>();
            Require((await state.GetValueAsync()).Counter == 7, "JSON file under user data");
            var dispatcher = new GodotConfiglueDispatcher();
            Require(dispatcher.CheckAccess(), "engine main thread");
            var threadId = Environment.CurrentManagedThreadId;
            var posted = new TaskCompletionSource<int>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            await Task.Run(() =>
            {
                Require(!dispatcher.CheckAccess(), "background access check");
                dispatcher.Post(() => posted.TrySetResult(Environment.CurrentManagedThreadId));
            });
            Require(
                await posted.Task.WaitAsync(TimeSpan.FromSeconds(10)) == threadId,
                "native deferred dispatch"
            );

            var invokedThread = 0;
            await Task.Run(async () =>
                await dispatcher.InvokeAsync(() =>
                    invokedThread = Environment.CurrentManagedThreadId
                )
            );
            Require(invokedThread == threadId, "awaitable native deferred dispatch");

            using var reader = new ConfiglueStateReader<SmokeSettings>(state, dispatcher);
            await reader.InitializeAsync();
            Require(((SmokeSettings.Observable)reader.Value!).Counter == 7, "generated observable");
            var changed = new TaskCompletionSource<int>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            reader.PropertyChanged += (_, args) =>
            {
                if (
                    args.PropertyName == nameof(reader.Value)
                    && ((SmokeSettings.Observable)reader.Value!).Counter == 8
                )
                    changed.TrySetResult(Environment.CurrentManagedThreadId);
            };
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (
                !context.GetDiagnostics<SmokeSettings>().GetRuntimeSnapshot().Sources[0].IsWatching
            )
            {
                Require(DateTime.UtcNow < deadline, "watcher startup");
                await Task.Delay(10);
            }
            await source.WriteAsync(
                ConfiglueResourceContext.Default,
                new StateWriteRequest<SmokeSettings.Fragment>(
                    new() { Counter = 8 },
                    RevisionCondition.Match(written.Revision!)
                )
            );
            Require(
                await changed.Task.WaitAsync(TimeSpan.FromSeconds(10)) == threadId,
                "background reload to observable UI update"
            );
            G.GD.Print("CONFIGLUE_GODOT_SMOKE_PASS");
            exitCode = 0;
        }
        catch (Exception exception)
        {
            G.GD.PushError(exception.ToString());
        }
        finally
        {
            if (temporaryDirectory is not null)
            {
                var userRoot = Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(G.ProjectSettings.GlobalizePath("user://"))
                );
                Require(
                    Path.GetDirectoryName(Path.GetFullPath(temporaryDirectory)) == userRoot,
                    "cleanup stays under project user data"
                );
                Directory.Delete(temporaryDirectory, recursive: true);
            }
            GetTree().Quit(exitCode);
        }
    }

    private static void Require(bool condition, string name)
    {
        if (!condition)
            throw new InvalidOperationException("Godot smoke failed: " + name);
    }
}

[ConfiglueModel("godot-smoke-settings", Version = 1)]
public partial class SmokeSettings
{
    public int Counter { get; set; }
}
