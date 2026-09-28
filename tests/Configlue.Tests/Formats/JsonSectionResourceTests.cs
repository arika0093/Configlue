using System.Text;
using System.Text.Json;
using Configlue.Provider.Json;
using Configlue.Testing;

namespace Configlue.Tests;

public sealed class JsonSectionResourceTests
{
    [Test]
    public async Task SectionResource_UpdatesNestedSectionAndPreservesSiblings()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "Configlue.Tests",
            Guid.NewGuid().ToString("N")
        );
        var path = System.IO.Path.Combine(directory, "appsettings.json");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            path,
            """
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
            """
        );
        using var file = new FileResource(path, new FileResourceOptions { CreateBackup = false });
        var section = new JsonSectionResource(file, "App:Settings");
        var codec = new JsonStateCodec<AppSettings.Fragment>();
        var reader = new SerializedStateReader<AppSettings.Fragment>(section, codec);
        var writer = new SerializedStateWriter<AppSettings.Fragment>(section, codec);
        var sources = new StateSourceSet<AppSettings.Fragment>([
            new("app-settings", reader, writer: writer, watcher: section),
        ]);
        var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(sources);

        try
        {
            var before = await options.ReadAsync();
            await options.SaveAsync(
                new AppSettings.Patch { RetryCount = FragmentOperation<int>.Set(9) }
            );
            using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(path));
            var root = document.RootElement;

            (before.Value!.RetryCount).ShouldBe(4);
            var settings = root.GetProperty("App").GetProperty("Settings");
            // The default simple layout stores the version inline.
            (settings.GetProperty("$version").GetInt32()).ShouldBe(2);
            (settings.GetProperty("RetryCount").GetInt32()).ShouldBe(9);
            (
                root.GetProperty("App").GetProperty("Other").GetProperty("Value").GetString()
            ).ShouldBe("keep-nested");
            (root.GetProperty("OtherSection").GetProperty("Value").GetString()).ShouldBe(
                "keep-root"
            );
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
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "Configlue.Tests",
            Guid.NewGuid().ToString("N")
        );
        var path = System.IO.Path.Combine(directory, "settings.json");
        using var file = new FileResource(path, new FileResourceOptions { CreateBackup = false });
        var section = new JsonSectionResource(file, "App__Settings");

        try
        {
            var initialSectionWrite = await section.WriteAsync(
                new ResourceWriteRequest(Encoding.UTF8.GetBytes("{\"value\":1}"))
            );
            var sectionRead = await section.ReadAsync();
            await File.WriteAllTextAsync(
                path,
                "{\"Other\":{\"changed\":true},\"App\":{\"Settings\":{\"value\":1}}}"
            );
            var conflict = false;
            try
            {
                await section.WriteAsync(
                    new ResourceWriteRequest(
                        Encoding.UTF8.GetBytes("{\"value\":2}"),
                        Condition: RevisionCondition.FromRevision(sectionRead.Revision)
                    )
                );
            }
            catch (StateConflictException)
            {
                conflict = true;
            }

            var final = await File.ReadAllTextAsync(path);
            (initialSectionWrite.Revision).ShouldNotBeNull();
            (sectionRead.Status).ShouldBe(StateReadStatus.Success);
            (conflict).ShouldBeTrue();
            (final).ShouldContain("\"changed\":true");
            (final).ShouldContain("\"value\":1");
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

    [Test]
    public async Task SectionResource_AcceptsJsoncAndPreservesCommentsAndFormatting()
    {
        var resource = new InMemoryResource();
        var source = """
            {
              // Keep this root comment.
              "App": {
                "Settings": {
                  // Keep this section comment.
                  "value": 1,
                },
                "Other": { "value": "keep" },
              },
              "Root": true,
            }
            """;
        await resource.WriteAsync(new ResourceWriteRequest(Encoding.UTF8.GetBytes(source)));
        var section = new JsonSectionResource(resource, "App:Settings");

        var read = await section.ReadAsync();
        read.Status.ShouldBe(StateReadStatus.Success);
        await section.WriteAsync(
            new ResourceWriteRequest(Encoding.UTF8.GetBytes("""{"value":2}"""))
        );

        var written = Encoding.UTF8.GetString((await resource.ReadAsync()).Content.Span);
        written.ShouldContain("// Keep this root comment.");
        written.ShouldContain("// Keep this section comment.");
        written.ShouldContain("\"Other\": { \"value\": \"keep\" }");
        written.ShouldContain("\"value\": 2");

        using var document = JsonDocument.Parse(
            written,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            }
        );
        document
            .RootElement.GetProperty("App")
            .GetProperty("Settings")
            .GetProperty("value")
            .GetInt32()
            .ShouldBe(2);
    }
}
