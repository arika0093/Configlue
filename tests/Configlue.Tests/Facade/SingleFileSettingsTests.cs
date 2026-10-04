using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;
using Configlue.Provider.Json;

namespace Configlue.Tests;

// Mirrors the README Quick Start model so documentation drift is caught by the
// golden-path test below (which replays the same initialize/read/save flow).
[ConfiglueModel("single-file-sample.settings", Version = 1)]
public partial class SingleFileSampleSettings
{
    public string Name { get; set; } = "World";

    public string Theme { get; set; } = "System";

    [Range(0, 100)]
    public int RetryCount { get; set; } = 3;
}

/// <summary>Golden-path coverage for the zero-ceremony single-file settings workflow (#231).</summary>
/// <remarks>
/// Uses only the convenience <c>Configlue</c> package surface and documented public APIs:
/// one model declaration, one <c>UseLocalJson</c> file declaration, and the shared
/// <c>IWritableState&lt;T&gt;</c> consumer calls also shown in the README Quick Start.
/// </remarks>
public sealed class SingleFileSettingsTests
{
    [Test]
    public async Task SingleFileGoldenPath()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "settings.json");

        // A missing file is not an error: reads observe model defaults ...
        await using var context = ConfiglueApp.CreateContext(config =>
        {
            config.Add<SingleFileSampleSettings>().UseLocalJson(path);
        });
        var settings = context.GetState<SingleFileSampleSettings>();
        var current = await settings.GetValueAsync();
        (current.Name).ShouldBe("World");
        (current.Theme).ShouldBe("System");
        (current.RetryCount).ShouldBe(3);

        // ... and the file is created by the first save.
        (File.Exists(path)).ShouldBeFalse();
        await settings.SaveAsync(patch =>
        {
            patch.Theme = "Dark";
        });
        (File.Exists(path)).ShouldBeTrue();
        (await settings.GetValueAsync()).Theme.ShouldBe("Dark");
        (await settings.GetValueAsync()).Name.ShouldBe("World");

        // Restarting the context persists the saved value.
        await context.DisposeAsync();
        await using var restarted = ConfiglueApp.CreateContext(config =>
        {
            config.Add<SingleFileSampleSettings>().UseLocalJson(path);
        });
        var reloaded = restarted.GetState<SingleFileSampleSettings>();
        (await reloaded.GetValueAsync()).Theme.ShouldBe("Dark");

        // External file changes reload through the default watcher.
        var observedTheme = string.Empty;
        var observedGate = new object();
        using var subscription = reloaded.OnChange(changed =>
        {
            lock (observedGate)
            {
                observedTheme = changed.Theme;
            }
        });
        await WriteExternalAsync(
            path,
            new JsonObject
            {
                ["$version"] = 1,
                ["Name"] = "World",
                ["Theme"] = "Light",
                ["RetryCount"] = 3,
            }.ToJsonString()
        );
        // Level-triggered watchers converge by re-reading: if a platform coalesces
        // or drops a single rapid file-watch edge, re-issuing the write recovers.
        // A genuinely broken watcher still fails via the timeout below.
        var lightJson = new JsonObject
        {
            ["$version"] = 1,
            ["Name"] = "World",
            ["Theme"] = "Light",
            ["RetryCount"] = 3,
        }.ToJsonString();
        await WriteExternalAsync(path, lightJson);
        await WaitUntilAsync(
            () =>
            {
                lock (observedGate)
                {
                    return observedTheme == "Light";
                }
            },
            async () => await WriteExternalAsync(path, lightJson),
            async () =>
            {
                string file;
                try
                {
                    file = await File.ReadAllTextAsync(path);
                }
                catch (Exception exception)
                {
                    file = "<unreadable: " + exception.GetType().Name + ">";
                }

                string current;
                try
                {
                    current = (await reloaded.GetValueAsync()).Theme;
                }
                catch (Exception exception)
                {
                    current = "<unreadable: " + exception.GetType().Name + ">";
                }

                string seen;
                lock (observedGate)
                {
                    seen = observedTheme;
                }

                return "observed='" + seen + "' file=" + file + " current=" + current;
            }
        );
        (await reloaded.GetValueAsync()).Theme.ShouldBe("Light");

        // A malformed document fails reads instead of silently returning defaults.
        await WriteExternalAsync(path, "{ this is not json");
        await Should.ThrowAsync<JsonException>(async () => await reloaded.GetValueAsync());

        // Restoring a valid document recovers reads ...
        await WriteExternalAsync(
            path,
            new JsonObject
            {
                ["$version"] = 1,
                ["Name"] = "World",
                ["Theme"] = "Light",
                ["RetryCount"] = 3,
            }.ToJsonString()
        );
        (await reloaded.GetValueAsync()).Theme.ShouldBe("Light");

        // ... while validation failures throw on both reads and saves.
        await WriteExternalAsync(
            path,
            new JsonObject
            {
                ["$version"] = 1,
                ["Name"] = "World",
                ["Theme"] = "Light",
                ["RetryCount"] = 1000,
            }.ToJsonString()
        );
        await Should.ThrowAsync<ConfiglueValidationException>(async () =>
            await reloaded.GetValueAsync()
        );

        // A valid document followed by an invalid patch fails the save instead of persisting it.
        await WriteExternalAsync(
            path,
            new JsonObject
            {
                ["$version"] = 1,
                ["Name"] = "World",
                ["Theme"] = "Light",
                ["RetryCount"] = 3,
            }.ToJsonString()
        );
        await Should.ThrowAsync<ConfiglueValidationException>(async () =>
            await reloaded.SaveAsync(patch =>
            {
                patch.RetryCount = 1000;
            })
        );
        (await reloaded.GetValueAsync()).RetryCount.ShouldBe(3);
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        Func<Task>? poke = null,
        Func<Task<string>>? describe = null
    )
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var pokes = 0;
        while (!condition())
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                var detail = describe is null ? string.Empty : " " + await describe();
                throw new TimeoutException(
                    "Timed out waiting for the watched file change to arrive." + detail
                );
            }

            // Re-issue the triggering write a few times while waiting so a single
            // coalesced file-watch edge cannot stall the test forever.
            if (poke is not null && ++pokes % 20 == 0)
            {
                await poke();
            }
        }
    }

    private static async Task WriteExternalAsync(string path, string content)
    {
        // External edits race the runtime's own watcher-triggered re-reads, which open
        // the file without write sharing. Retry transient sharing violations instead of
        // failing the golden path on scheduling luck. (Two-argument async file APIs keep
        // this helper compatible with the net48 test target.)
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            try
            {
                await File.WriteAllTextAsync(path, content);
                return;
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50));
            }
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            FullPath = Path.Combine(
                Path.GetTempPath(),
                "Configlue.Tests",
                Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(FullPath);
        }

        public string FullPath { get; }

        public void Dispose()
        {
            if (Directory.Exists(FullPath))
            {
                Directory.Delete(FullPath, recursive: true);
            }
        }
    }
}
