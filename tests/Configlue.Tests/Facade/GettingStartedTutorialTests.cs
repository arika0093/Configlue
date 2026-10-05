using Configlue.Provider.Json;

namespace Configlue.Tests;

// Compile-checked mirror of docs/{en,ja}/getting-started/index.mdx (#323).
// Keeps the tutorial's canonical sample (AppSettings with Name/Theme,
// ConfiglueApp.Initialize + UseLocalJson + GetValueAsync/SaveAsync/OnChange +
// ShutdownAsync) compiling against the generator output. Behavioral depth
// (watcher recovery, malformed documents, validation) stays in
// SingleFileSettingsTests; this test only guards the tutorial's first-success
// path and its process-wide lifecycle.
[ConfiglueModel("sample.settings", Version = 1)]
public partial class GettingStartedSampleSettings
{
    public string Name { get; set; } = "World";

    public string Theme { get; set; } = "System";
}

/// <summary>Guards the Getting Started first-success path from local JSON to save.</summary>
public sealed class GettingStartedTutorialTests
{
    [Test]
    [NotInParallel]
    public async Task TutorialFlowReadsDefaultsThenSaves()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "configlue-getting-started-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");

        await ConfiglueApp.ShutdownAsync();
        try
        {
            ConfiglueApp.Initialize(config =>
            {
                config.Add<GettingStartedSampleSettings>().UseLocalJson(path);
            });

            var settings = ConfiglueApp.GetState<GettingStartedSampleSettings>();

            // 3. Read typed state: a missing file reads as model defaults.
            var current = await settings.GetValueAsync();
            (current.Name).ShouldBe("World");
            (current.Theme).ShouldBe("System");
            (File.Exists(path)).ShouldBeFalse();

            // 5. Observe a change: same primary state surface as the tutorial.
            using var subscription = settings.OnChange(changed =>
            {
                _ = changed.Theme;
            });

            // 4. Save a change through the generated sparse patch.
            await settings.SaveAsync(patch =>
            {
                patch.Name = "Alice";
                patch.Theme = "Dark";
            });

            (File.Exists(path)).ShouldBeTrue();
            var saved = await settings.GetValueAsync();
            (saved.Name).ShouldBe("Alice");
            (saved.Theme).ShouldBe("Dark");
        }
        finally
        {
            await ConfiglueApp.ShutdownAsync();
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException) { }
        }
    }
}
