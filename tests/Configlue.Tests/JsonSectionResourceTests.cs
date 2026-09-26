using System.Text;
using System.Text.Json;
using Configlue.Provider.Json;

namespace Configlue.Tests;

public sealed class JsonSectionResourceTests
{
    [Test]
    public async Task SectionResource_UpdatesNestedSectionAndPreservesSiblings()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Configlue.Tests", Guid.NewGuid().ToString("N"));
        var path = System.IO.Path.Combine(directory, "appsettings.json");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path, """
        {
          "App": {
            "Settings": {
              "$configlue": { "id": "app-settings", "version": 2 },
              "$value": { "RetryCount": 4 }
            },
            "Other": { "Value": "keep-nested" }
          },
          "OtherSection": { "Value": "keep-root" }
        }
        """);
        using var file = new FileResource(path, new FileResourceOptions { CreateBackup = false });
        var section = new JsonSectionResource(file, "App:Settings");
        var codec = new JsonStateCodec<AppSettings.Fragment>();
        var reader = new SerializedStateReader<AppSettings.Fragment>(section, codec);
        var writer = new SerializedStateWriter<AppSettings.Fragment>(section, codec);
        var sources = new StateSourceSet<AppSettings.Fragment>([new("app-settings", reader, writer: writer, watcher: section)]);
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(sources);

        try
        {
            var before = await options.ReadAsync();
            await options.ApplyPatchAsync(new AppSettings.Patch
            {
                RetryCount = FragmentOperation<int>.Set(9),
            });
            using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(path));
            var root = document.RootElement;

            await Assert.That(before.Value!.RetryCount).IsEqualTo(4);
            await Assert.That(root.GetProperty("App").GetProperty("Settings").GetProperty("$value").GetProperty("RetryCount").GetInt32())
                .IsEqualTo(9);
            await Assert.That(root.GetProperty("App").GetProperty("Other").GetProperty("Value").GetString())
                .IsEqualTo("keep-nested");
            await Assert.That(root.GetProperty("OtherSection").GetProperty("Value").GetString())
                .IsEqualTo("keep-root");
        }
        finally
        {
            file.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task SectionResource_CreatesMissingNestedPathAndDetectsStaleWholeDocumentRevision()
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Configlue.Tests", Guid.NewGuid().ToString("N"));
        var path = System.IO.Path.Combine(directory, "settings.json");
        using var file = new FileResource(path, new FileResourceOptions { CreateBackup = false });
        var section = new JsonSectionResource(file, "App__Settings");

        try
        {
            var initialSectionWrite = await section.WriteAsync(new ResourceWriteRequest(Encoding.UTF8.GetBytes("{\"value\":1}")));
            var sectionRead = await section.ReadAsync();
            await File.WriteAllTextAsync(path, "{\"Other\":{\"changed\":true},\"App\":{\"Settings\":{\"value\":1}}}");
            var conflict = false;
            try
            {
                await section.WriteAsync(new ResourceWriteRequest(
                    Encoding.UTF8.GetBytes("{\"value\":2}"), sectionRead.Revision, CheckRevision: true));
            }
            catch (StateConflictException)
            {
                conflict = true;
            }

            var final = await File.ReadAllTextAsync(path);
            await Assert.That(initialSectionWrite.Revision).IsNotNull();
            await Assert.That(sectionRead.Status).IsEqualTo(StateReadStatus.Success);
            await Assert.That(conflict).IsTrue();
            await Assert.That(final).Contains("\"changed\":true");
            await Assert.That(final).Contains("\"value\":1");
        }
        finally
        {
            file.Dispose();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
