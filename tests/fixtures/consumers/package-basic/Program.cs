using Configlue;
using Configlue.Source.Presets;
using PackageBasic;

var directory = Path.Combine(
    Path.GetTempPath(),
    "configlue-package-basic-" + Guid.NewGuid().ToString("N")
);
Directory.CreateDirectory(directory);
var path = Path.Combine(directory, "settings.json");

try
{
    await using (
        var context = ConfiglueApp.CreateContext(conf =>
            conf.UseCommonSources(sources =>
            {
                sources.WithExplicit(path);
                sources.Add<SampleSetting>();
            })
        )
    )
    {
        var state = context.GetState<SampleSetting>();
        var current = await state.GetValueAsync();
        Require(current.Name == "World", $"Unexpected default name '{current.Name}'.");
        Require(current.RunCount == 0, $"Unexpected default run count '{current.RunCount}'.");

        await state.SaveAsync(patch =>
        {
            patch.Name = "Alice";
            patch.RunCount = current.RunCount + 1;
        });
    }

    await using (
        var context = ConfiglueApp.CreateContext(conf =>
            conf.UseCommonSources(sources =>
            {
                sources.WithExplicit(path);
                sources.Add<SampleSetting>();
            })
        )
    )
    {
        var persisted = await context.GetState<SampleSetting>().GetValueAsync();
        Require(persisted.Name == "Alice", $"Expected persisted name 'Alice', got '{persisted.Name}'.");
        Require(persisted.RunCount == 1, $"Expected persisted run count 1, got '{persisted.RunCount}'.");
    }

    Console.WriteLine("CONFIGLUE_PACKAGE_BASIC_PASS");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}
finally
{
    try
    {
        Directory.Delete(directory, recursive: true);
    }
    catch (IOException) { }
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
