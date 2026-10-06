using Configlue;
using Configlue.Examples.Shared;
using Configlue.Provider.Json;
using Shouldly;
using TUnit.Core;

namespace Configlue.Examples.Smoke;

// Lightweight smoke coverage for the Playground local-settings scenario:
// the UseLocalJson golden path (defaults, save, validation, external edits).
// No infrastructure is required.
public sealed class LocalSettingsSmokeTests
{
    [Test]
    public async Task LocalSettings_ReadDefaultsThenSave()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "settings.json");

        await using var context = ConfiglueApp.CreateContext(config =>
        {
            config.Add<LocalSettings>().UseLocalJson(path);
        });
        var settings = context.GetState<LocalSettings>();

        // A missing file reads as model defaults and is created by the first save.
        (await settings.GetValueAsync()).Theme.ShouldBe("System");
        File.Exists(path).ShouldBeFalse();

        await settings.SaveAsync(patch =>
        {
            patch.Name = "Alice";
            patch.Theme = "Dark";
        });

        File.Exists(path).ShouldBeTrue();
        var saved = await settings.GetValueAsync();
        saved.Name.ShouldBe("Alice");
        saved.Theme.ShouldBe("Dark");
    }

    [Test]
    public async Task LocalSettings_ExternalEditIsObserved()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "settings.json");

        await using var context = ConfiglueApp.CreateContext(config =>
        {
            config.Add<LocalSettings>().UseLocalJson(path);
        });
        var settings = context.GetState<LocalSettings>();
        await settings.SaveAsync(patch =>
        {
            patch.Theme = "Dark";
        });

        string? observed = null;
        using var subscription = settings.OnChange(changed =>
            Interlocked.Exchange(ref observed, changed.Theme)
        );

        // Level-triggered watchers converge by re-reading: re-issue the write
        // while polling so a coalesced file-watch edge still converges.
        for (
            var attempt = 0;
            attempt < 100 && Volatile.Read(ref observed) != "Light";
            attempt++
        )
        {
            await File.WriteAllTextAsync(
                path,
                """{"$version":1,"Name":"World","Theme":"Light","RetryCount":3}"""
            );
            await Task.Delay(200);
        }

        Volatile.Read(ref observed).ShouldBe("Light");
        (await settings.GetValueAsync()).Theme.ShouldBe("Light");
    }

    [Test]
    public async Task LocalSettings_InvalidSaveFailsValidation()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "settings.json");

        await using var context = ConfiglueApp.CreateContext(config =>
        {
            config.Add<LocalSettings>().UseLocalJson(path);
        });
        var settings = context.GetState<LocalSettings>();

        var failure = await Should.ThrowAsync<ConfiglueValidationException>(
            async () =>
                await settings.SaveAsync(patch =>
                {
                    patch.RetryCount = 1000;
                })
        );
        failure.Failures.Count.ShouldBeGreaterThan(0);
    }
}
