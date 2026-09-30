using System.Buffers;
using System.Text;
using System.Xml.Linq;
using Configlue.Provider.Json;
using Configlue.Provider.Xml;
using Configlue.Provider.Yaml;
using Configlue.Testing;
using SharpYaml;

namespace Configlue.Tests;

public sealed class FormatSectionResourceTests
{
    [Test]
    public async Task JsonSectionResource_RecoversMalformedDocumentAfterSectionAndCodecValidation()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "ConfiglueTests",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        try
        {
            var path = System.IO.Path.Combine(directory, "settings.json");
            var codec = new JsonStateCodec<AppSettings.Fragment>();
            var section = Serialize(
                codec,
                new AppSettings.Fragment { RetryCount = Optional<int>.Present(4) }
            );
            var backup =
                "{\"App\":{\"Settings\":"
                + Encoding.UTF8.GetString(section)
                + ",\"Sibling\":\"preserved\"}}";
            const string malformed = "{ malformed document";
            await File.WriteAllTextAsync(path, malformed);
            await File.WriteAllTextAsync(GetLegacyBackupPath(path), backup);
            using var resource = new FileResource(
                path,
                new FileResourceOptions { AutomaticBackupRecovery = true }
            );
            var reader = new SerializedStateReader<AppSettings.Fragment>(
                new JsonSectionResource(resource, "App:Settings"),
                codec
            );

            var recovered = await reader.ReadAsync();

            recovered.Status.ShouldBe(StateReadStatus.Success);
            recovered.Value!.RetryCount.ShouldBe(4);
            (await File.ReadAllTextAsync(path)).ShouldBe(backup);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task JsonSectionResource_RecoversMissingFileBeforeFallbackSelection()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "ConfiglueTests",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        try
        {
            var path = System.IO.Path.Combine(directory, "settings.json");
            var codec = new JsonStateCodec<AppSettings.Fragment>();
            var section = Serialize(
                codec,
                new AppSettings.Fragment { RetryCount = Optional<int>.Present(4) }
            );
            var backup =
                "{\"App\":{\"Settings\":"
                + Encoding.UTF8.GetString(section)
                + ",\"Sibling\":\"preserved\"}}";
            await File.WriteAllTextAsync(GetLegacyBackupPath(path), backup);
            using var resource = new FileResource(
                path,
                new FileResourceOptions { AutomaticBackupRecovery = true }
            );
            var primarySource = SerializedStateSource.FromResource<AppSettings.Fragment>(
                "primary-file",
                new JsonSectionResource(resource, "App:Settings"),
                codec,
                priority: 100
            );
            var fallbackSource = new StateSource<AppSettings.Fragment>(
                "fallback",
                new InMemoryStateStore<AppSettings.Fragment>(
                    new AppSettings.Fragment { RetryCount = Optional<int>.Present(99) }
                )
            );
            await using var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
                new StateSourceSet<AppSettings.Fragment>([primarySource, fallbackSource])
            );

            var recovered = await options.ReadAsync();

            recovered.Status.ShouldBe(StateReadStatus.Success);
            recovered.Value!.RetryCount.ShouldBe(4);
            recovered.SourceId.ShouldBe("primary-file");
            (await File.ReadAllTextAsync(path)).ShouldBe(backup);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task JsonSectionResource_LeavesValidDocumentWithMissingSectionUntouched()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "ConfiglueTests",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        try
        {
            var path = System.IO.Path.Combine(directory, "settings.json");
            const string current = "{\"App\":{\"Sibling\":\"current\"}}";
            var codec = new JsonStateCodec<AppSettings.Fragment>();
            var backupSection = Serialize(
                codec,
                new AppSettings.Fragment { RetryCount = Optional<int>.Present(4) }
            );
            var backup = "{\"App\":{\"Settings\":" + Encoding.UTF8.GetString(backupSection) + "}}";
            await File.WriteAllTextAsync(path, current);
            await File.WriteAllTextAsync(GetLegacyBackupPath(path), backup);
            using var resource = new FileResource(
                path,
                new FileResourceOptions { AutomaticBackupRecovery = true }
            );
            var reader = new SerializedStateReader<AppSettings.Fragment>(
                new JsonSectionResource(resource, "App:Settings"),
                codec
            );

            var result = await reader.ReadAsync();

            result.Status.ShouldBe(StateReadStatus.NotFound);
            (await File.ReadAllTextAsync(path)).ShouldBe(current);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task XmlSectionResource_RecoversMalformedDocumentAndPreservesValidatedBackup()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "ConfiglueTests",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        try
        {
            var path = System.IO.Path.Combine(directory, "settings.xml");
            var codec = new XmlStateCodec<AppSettings.Fragment>();
            var section = Serialize(
                codec,
                new AppSettings.Fragment { RetryCount = Optional<int>.Present(4) }
            );
            var sectionDocument = XDocument.Parse(Encoding.UTF8.GetString(section));
            var backup =
                "<configuration><App><Settings>"
                + sectionDocument.Root!.ToString(SaveOptions.DisableFormatting)
                + "</Settings><Sibling>preserved</Sibling></App></configuration>";
            const string malformed = "<configuration><App>";
            await File.WriteAllTextAsync(path, malformed);
            await File.WriteAllTextAsync(GetLegacyBackupPath(path), backup);
            using var resource = new FileResource(
                path,
                new FileResourceOptions { AutomaticBackupRecovery = true }
            );
            var reader = new SerializedStateReader<AppSettings.Fragment>(
                new XmlSectionResource(resource, "App:Settings"),
                codec
            );

            var recovered = await reader.ReadAsync();

            recovered.Status.ShouldBe(StateReadStatus.Success);
            recovered.Value!.RetryCount.ShouldBe(4);
            (await File.ReadAllTextAsync(path)).ShouldBe(backup);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task YamlSectionResource_RecoversMalformedDocumentAndPreservesValidatedBackup()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "ConfiglueTests",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        try
        {
            var path = System.IO.Path.Combine(directory, "settings.yaml");
            var codec = new YamlStateCodec<AppSettings.Fragment>(
                modelSchema: AppSettings.FragmentSchema
            );
            var backup =
                "App:"
                + Environment.NewLine
                + "  Settings:"
                + Environment.NewLine
                + "    RetryCount: 4"
                + Environment.NewLine
                + "  Sibling: preserved"
                + Environment.NewLine;
            const string malformed = "App: [unterminated";
            await File.WriteAllTextAsync(path, malformed);
            await File.WriteAllTextAsync(GetLegacyBackupPath(path), backup);
            using var resource = new FileResource(
                path,
                new FileResourceOptions { AutomaticBackupRecovery = true }
            );
            var reader = new SerializedStateReader<AppSettings.Fragment>(
                new YamlSectionResource(resource, "App:Settings"),
                codec
            );

            var recovered = await reader.ReadAsync();

            recovered.Status.ShouldBe(StateReadStatus.Success);
            recovered.Value!.RetryCount.ShouldBe(4);
            (await File.ReadAllTextAsync(path)).ShouldBe(backup);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task XmlSectionResource_UpdatesNestedGeneratedFragmentAndPreservesSiblings()
    {
        var codec = new XmlStateCodec<AppSettings.Fragment>();
        var fragmentBytes = Serialize(
            codec,
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(4) }
        );
        var document = new XDocument(
            new XElement(
                "configuration",
                new XElement(
                    "App",
                    new XElement(
                        "Settings",
                        XElement.Parse(Encoding.UTF8.GetString(fragmentBytes))
                    ),
                    new XElement("Other", new XElement("Value", "keep-nested"))
                ),
                new XElement("OtherSection", new XElement("Value", "keep-root"))
            )
        );
        var resource = new InMemoryResource();
        await resource.WriteAsync(
            new ResourceWriteRequest(
                Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting))
            )
        );
        var section = new XmlSectionResource(resource, "App__Settings");
        var source = SerializedStateSource.FromResource<AppSettings.Fragment>(
            "settings",
            section,
            codec
        );
        var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([source])
        );

        await options.SaveAsync(
            new AppSettings.Patch { RetryCount = FragmentOperation<int>.Set(9) }
        );

        var updated = XDocument.Parse(
            Encoding.UTF8.GetString((await resource.ReadAsync()).Content.Span)
        );
        var root = updated.Root!;
        var sectionValue =
            root.Element("App")?.Element("Settings")?.Element("configlue")
            ?? throw new InvalidOperationException(updated.ToString(SaveOptions.None));
        var retryCount = sectionValue
            .Elements("member")
            .Single(element => (string?)element.Attribute("name") == "RetryCount");
        ((string?)retryCount.Element("int")).ShouldBe("9");
        (root.Element("App")!.Element("Other")!.Element("Value")!.Value).ShouldBe("keep-nested");
        (root.Element("OtherSection")!.Element("Value")!.Value).ShouldBe("keep-root");
    }

    [Test]
    public async Task XmlSectionResource_CreatesMissingDocumentAndNestedPath()
    {
        var resource = new InMemoryResource();
        var section = new XmlSectionResource(resource, "App__Settings");
        var source = SerializedStateSource.FromResource<AppSettings.Fragment>(
            "settings",
            section,
            new XmlStateCodec<AppSettings.Fragment>()
        );
        await using var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([source])
        );

        await options.SaveAsync(
            new AppSettings.Patch { RetryCount = FragmentOperation<int>.Set(7) }
        );

        var document = XDocument.Parse(
            Encoding.UTF8.GetString((await resource.ReadAsync()).Content.Span)
        );
        var retryCount = document
            .Root!.Element("App")!
            .Element("Settings")!
            .Element("configlue")!
            .Elements("member")
            .Single(element => (string?)element.Attribute("name") == "RetryCount");
        ((string?)retryCount.Element("int")).ShouldBe("7");
    }

    [Test]
    public async Task YamlSectionResource_UpdatesNestedGeneratedFragmentAndPreservesSiblings()
    {
        var codec = new YamlStateCodec<AppSettings.Fragment>(
            modelSchema: AppSettings.FragmentSchema
        );
        var resource = new InMemoryResource();
        var yamlWithComments = """
            # Keep the root comment.
            App:
              # Keep the section comment.
              Settings:
                $version: 2
                RetryCount: 4 # Keep the inline comment.
              Other:
                Value: keep-nested
            OtherSection:
              Value: keep-root
            # Keep the trailing comment.
            """.Replace("\r\n", "\n").Replace(
            "\n",
            "\r\n",
            StringComparison.Ordinal
        );
        var textEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        var preamble = textEncoding.GetPreamble();
        var originalBytes = new byte[
            preamble.Length + Encoding.UTF8.GetByteCount(yamlWithComments)
        ];
        preamble.CopyTo(originalBytes, 0);
        Encoding.UTF8.GetBytes(yamlWithComments, originalBytes.AsSpan(preamble.Length));
        await resource.WriteAsync(new ResourceWriteRequest(originalBytes));
        var section = new YamlSectionResource(
            resource,
            resource,
            "App:Settings",
            textEncoding: textEncoding
        );
        var source = SerializedStateSource.FromResource<AppSettings.Fragment>(
            "settings",
            section,
            codec
        );
        var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([source])
        );

        await options.SaveAsync(
            new AppSettings.Patch { RetryCount = FragmentOperation<int>.Set(9) }
        );

        var updatedBytes = (await resource.ReadAsync()).Content.ToArray();
        updatedBytes.AsSpan(0, preamble.Length).SequenceEqual(preamble).ShouldBeTrue();
        var updatedText = Encoding.UTF8.GetString(updatedBytes.AsSpan(preamble.Length));
        updatedText.Replace("\r\n", "").ShouldNotContain("\n");
        var updatedRoot = LoadYaml(Encoding.UTF8.GetBytes(updatedText));
        var app = GetMapping(updatedRoot, "App");
        var settings = GetMapping(app, "Settings");
        // The default simple layout stores the version inline.
        GetNode(settings, "$version").ShouldBe(2);
        GetNode(settings, "RetryCount").ShouldBe(9);
        GetNode(GetMapping(app, "Other"), "Value").ShouldBe("keep-nested");
        GetNode(GetMapping(updatedRoot, "OtherSection"), "Value").ShouldBe("keep-root");
        updatedText.ShouldContain("# Keep the root comment.");
        updatedText.ShouldContain("# Keep the section comment.");
        updatedText.ShouldContain("# Keep the inline comment.");
        updatedText.ShouldContain("# Keep the trailing comment.");
    }

    [Test]
    public async Task YamlSectionResource_CreatesMissingDocumentAndNestedPath()
    {
        var resource = new InMemoryResource();
        var section = new YamlSectionResource(resource, "App:Settings");
        var source = SerializedStateSource.FromResource<AppSettings.Fragment>(
            "settings",
            section,
            new YamlStateCodec<AppSettings.Fragment>(modelSchema: AppSettings.FragmentSchema)
        );
        await using var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([source])
        );

        await options.SaveAsync(
            new AppSettings.Patch { RetryCount = FragmentOperation<int>.Set(7) }
        );

        var updatedRoot = LoadYaml((await resource.ReadAsync()).Content.Span);
        var values = GetMapping(GetMapping(updatedRoot, "App"), "Settings");
        GetNode(values, "$version").ShouldBe(2);
        GetNode(values, "RetryCount").ShouldBe(7);
    }

    [Test]
    public async Task YamlSectionResource_DoesNotOverwriteMalformedDocument()
    {
        const string malformed = "App:\n  Settings: [unterminated\n";
        var resource = new InMemoryResource();
        await resource.WriteAsync(new ResourceWriteRequest(Encoding.UTF8.GetBytes(malformed)));
        var section = new YamlSectionResource(resource, "App:Settings");
        var source = SerializedStateSource.FromResource<AppSettings.Fragment>(
            "settings",
            section,
            new YamlStateCodec<AppSettings.Fragment>(modelSchema: AppSettings.FragmentSchema)
        );
        await using var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([source])
        );

        await Should.ThrowAsync<SharpYaml.YamlException>(async () =>
            await options.SaveAsync(
                new AppSettings.Patch { RetryCount = FragmentOperation<int>.Set(7) }
            )
        );

        Encoding.UTF8.GetString((await resource.ReadAsync()).Content.Span).ShouldBe(malformed);
    }

    [Test]
    public async Task YamlSectionResource_RejectsDuplicateMappingKeysWithoutWriting()
    {
        const string duplicate = "App:\n  Settings:\n    RetryCount: 3\n    RetryCount: 4\n";
        var resource = new InMemoryResource();
        await resource.WriteAsync(new ResourceWriteRequest(Encoding.UTF8.GetBytes(duplicate)));
        var section = new YamlSectionResource(resource, "App:Settings");

        await Should.ThrowAsync<SharpYaml.YamlException>(async () => await section.ReadAsync());

        Encoding.UTF8.GetString((await resource.ReadAsync()).Content.Span).ShouldBe(duplicate);
    }

    private static byte[] Serialize<T>(IStateCodec<T> codec, T value)
    {
        var output = new ArrayBufferWriter<byte>();
        var context = default(StateCodecContext);
        codec.Serialize(value, output, in context);
        return output.WrittenSpan.ToArray();
    }

    private static string GetLegacyBackupPath(string path)
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(path)!,
            OperatingSystem.IsWindows() ? "backup" : ".backup"
        );
        Directory.CreateDirectory(directory);
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        if (!OperatingSystem.IsWindows())
        {
            name = "." + name;
        }

        name +=
            "_" + DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return System.IO.Path.Combine(directory, name + System.IO.Path.GetExtension(path) + ".bak");
    }

    private static object? LoadYaml(ReadOnlySpan<byte> content) =>
        YamlSerializer.Deserialize<object>(Encoding.UTF8.GetString(content));

    private static byte[] SerializeYaml(object? node) =>
        Encoding.UTF8.GetBytes(YamlSerializer.Serialize(node, typeof(object)));

    private static IDictionary<string, object?> GetMapping(object? node, string key) =>
        GetNode(node, key) as IDictionary<string, object?>
        ?? throw new InvalidOperationException($"YAML node '{key}' is not a mapping.");

    private static object? GetNode(object? node, string key)
    {
        if (node is not IDictionary<string, object?> mapping)
        {
            throw new InvalidOperationException("Expected a YAML mapping.");
        }

        return mapping[key];
    }
}
