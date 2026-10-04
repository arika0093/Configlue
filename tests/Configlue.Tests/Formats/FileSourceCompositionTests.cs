using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Configlue.Provider.Json;
using Configlue.Provider.Xml;
using Configlue.Provider.Yaml;

namespace Configlue.Tests;

/// <summary>
/// Covers JSON/YAML/XML file read/write/watch paths through the standard-layer
/// file composition (#260): providers contribute codecs and section views while
/// the standard layer owns the file resource.
/// </summary>
public sealed class FileSourceCompositionTests
{
    [Test]
    public async Task JsonFile_ReadWriteWatchThroughStandardComposition()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "settings.json");
        await WriteJsonFragmentAsync(
            path,
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(11) }
        );

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                    sources.FromJsonFile(
                        new JsonFileSourceOptions { Path = path, WatchChanges = false }
                    )
                )
            );
        });
        var state = context.GetState<AppSettings>();
        (await state.GetValueAsync()).RetryCount.ShouldBe(11);

        await state.SaveAsync(patch => patch.Label = "saved-json");
        (await state.GetValueAsync()).Label.ShouldBe("saved-json");
        (await File.ReadAllTextAsync(path)).ShouldContain("saved-json");

        await using var watcher = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                    sources.FromJsonFile(new JsonFileSourceOptions { Path = path })
                )
            );
        });
        await using var writer = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                    sources.FromJsonFile(
                        new JsonFileSourceOptions { Path = path, WatchChanges = false }
                    )
                )
            );
        });
        var observed = string.Empty;
        var observedGate = new object();
        using var subscription = watcher
            .GetState<AppSettings>()
            .OnChange(changed =>
            {
                lock (observedGate)
                {
                    observed = changed.Label;
                }
            });
        await writer.GetState<AppSettings>().SaveAsync(patch => patch.Label = "watched-json");
        await WaitUntilAsync(
            () =>
            {
                lock (observedGate)
                {
                    return observed == "watched-json";
                }
            },
            async () =>
                await writer
                    .GetState<AppSettings>()
                    .SaveAsync(patch => patch.Label = "watched-json")
        );
    }

    [Test]
    public async Task YamlFile_ReadWriteWatchThroughStandardComposition()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "settings.yaml");
        await WriteYamlFragmentAsync(
            path,
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(12) }
        );

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                    sources.FromYamlFile(
                        new YamlFileSourceOptions { Path = path, WatchChanges = false }
                    )
                )
            );
        });
        var state = context.GetState<AppSettings>();
        (await state.GetValueAsync()).RetryCount.ShouldBe(12);

        await state.SaveAsync(patch => patch.Label = "saved-yaml");
        (await state.GetValueAsync()).Label.ShouldBe("saved-yaml");
        var contents = await File.ReadAllTextAsync(path);
        contents.ShouldContain("saved-yaml");
        contents.ShouldNotContain("{");

        await using var watcher = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                    sources.FromYamlFile(new YamlFileSourceOptions { Path = path })
                )
            );
        });
        await using var writer = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                    sources.FromYamlFile(
                        new YamlFileSourceOptions { Path = path, WatchChanges = false }
                    )
                )
            );
        });
        var observed = string.Empty;
        var observedGate = new object();
        using var subscription = watcher
            .GetState<AppSettings>()
            .OnChange(changed =>
            {
                lock (observedGate)
                {
                    observed = changed.Label;
                }
            });
        await writer.GetState<AppSettings>().SaveAsync(patch => patch.Label = "watched-yaml");
        await WaitUntilAsync(
            () =>
            {
                lock (observedGate)
                {
                    return observed == "watched-yaml";
                }
            },
            async () =>
                await writer
                    .GetState<AppSettings>()
                    .SaveAsync(patch => patch.Label = "watched-yaml")
        );
    }

    [Test]
    public async Task XmlFile_ReadWriteWatchThroughStandardComposition()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "settings.xml");
        await WriteXmlFragmentAsync(
            path,
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(13) }
        );

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                    sources.FromXmlFile(
                        new XmlFileSourceOptions { Path = path, WatchChanges = false }
                    )
                )
            );
        });
        var state = context.GetState<AppSettings>();
        (await state.GetValueAsync()).RetryCount.ShouldBe(13);

        await state.SaveAsync(patch => patch.Label = "saved-xml");
        (await state.GetValueAsync()).Label.ShouldBe("saved-xml");
        (await File.ReadAllTextAsync(path)).ShouldContain("saved-xml");

        await using var watcher = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                    sources.FromXmlFile(new XmlFileSourceOptions { Path = path })
                )
            );
        });
        await using var writer = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                    sources.FromXmlFile(
                        new XmlFileSourceOptions { Path = path, WatchChanges = false }
                    )
                )
            );
        });
        var observed = string.Empty;
        var observedGate = new object();
        using var subscription = watcher
            .GetState<AppSettings>()
            .OnChange(changed =>
            {
                lock (observedGate)
                {
                    observed = changed.Label;
                }
            });
        await writer.GetState<AppSettings>().SaveAsync(patch => patch.Label = "watched-xml");
        await WaitUntilAsync(
            () =>
            {
                lock (observedGate)
                {
                    return observed == "watched-xml";
                }
            },
            async () =>
                await writer.GetState<AppSettings>().SaveAsync(patch => patch.Label = "watched-xml")
        );
    }

    [Test]
    public async Task JsonSection_ReadWritePreservesSiblings()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "settings.json");
        var section = SerializeJson(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(4) }
        );
        await File.WriteAllTextAsync(
            path,
            "{\"App\":{\"Settings\":"
                + Encoding.UTF8.GetString(section)
                + ",\"Other\":{\"Value\":\"keep-nested\"}},\"OtherSection\":{\"Value\":\"keep-root\"}}"
        );

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                    sources.FromJsonFile(
                        new JsonFileSourceOptions
                        {
                            Path = path,
                            SectionPath = "App:Settings",
                            WatchChanges = false,
                        }
                    )
                )
            );
        });
        var state = context.GetState<AppSettings>();
        (await state.GetValueAsync()).RetryCount.ShouldBe(4);

        await state.SaveAsync(patch => patch.RetryCount = 9);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var root = document.RootElement;
        root.GetProperty("App")
            .GetProperty("Settings")
            .GetProperty("RetryCount")
            .GetInt32()
            .ShouldBe(9);
        root.GetProperty("App")
            .GetProperty("Other")
            .GetProperty("Value")
            .GetString()
            .ShouldBe("keep-nested");
        root.GetProperty("OtherSection").GetProperty("Value").GetString().ShouldBe("keep-root");
    }

    [Test]
    public async Task YamlSection_ReadWritePreservesSiblings()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "settings.yaml");
        await File.WriteAllTextAsync(
            path,
            """
            App:
              Settings:
                $version: 2
                RetryCount: 4
              Other:
                Value: keep-nested
            OtherSection:
              Value: keep-root
            """
        );

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                    sources.FromYamlFile(
                        new YamlFileSourceOptions
                        {
                            Path = path,
                            SectionPath = "App:Settings",
                            WatchChanges = false,
                        }
                    )
                )
            );
        });
        var state = context.GetState<AppSettings>();
        (await state.GetValueAsync()).RetryCount.ShouldBe(4);

        await state.SaveAsync(patch => patch.RetryCount = 9);

        var contents = await File.ReadAllTextAsync(path);
        contents.ShouldContain("RetryCount: 9");
        contents.ShouldContain("keep-nested");
        contents.ShouldContain("keep-root");
    }

    [Test]
    public async Task XmlSection_ReadWritePreservesSiblings()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "settings.xml");
        var fragment = SerializeXml(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(4) }
        );
        var document = new XDocument(
            new XElement(
                "configuration",
                new XElement(
                    "App",
                    new XElement("Settings", XElement.Parse(Encoding.UTF8.GetString(fragment))),
                    new XElement("Other", new XElement("Value", "keep-nested"))
                ),
                new XElement("OtherSection", new XElement("Value", "keep-root"))
            )
        );
        await File.WriteAllTextAsync(path, document.ToString(SaveOptions.DisableFormatting));

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                    sources.FromXmlFile(
                        new XmlFileSourceOptions
                        {
                            Path = path,
                            SectionPath = "App__Settings",
                            WatchChanges = false,
                        }
                    )
                )
            );
        });
        var state = context.GetState<AppSettings>();
        (await state.GetValueAsync()).RetryCount.ShouldBe(4);

        await state.SaveAsync(patch => patch.RetryCount = 9);

        var updated = XDocument.Parse(await File.ReadAllTextAsync(path));
        var root = updated.Root!;
        var sectionValue =
            root.Element("App")?.Element("Settings")?.Element("configlue")
            ?? throw new InvalidOperationException(updated.ToString(SaveOptions.None));
        sectionValue
            .Elements("member")
            .Single(element => (string?)element.Attribute("name") == "RetryCount")
            .Element("int")!
            .Value.ShouldBe("9");
        root.Element("App")!.Element("Other")!.Element("Value")!.Value.ShouldBe("keep-nested");
        root.Element("OtherSection")!.Element("Value")!.Value.ShouldBe("keep-root");
    }

    [Test]
    public async Task TransformersApplyAroundFileContent()
    {
        foreach (var format in new[] { "json", "yaml" })
        {
            using var directory = new TemporaryDirectory();
            var path = Path.Combine(directory.FullPath, $"settings.{format}");
            var transformers = new IStateByteTransformer[] { new PrefixTransformer("guarded:") };

            await using var context = ConfiglueApp.CreateContext(builder =>
            {
                builder.Add<AppSettings>(model =>
                    model.Sources(sources =>
                    {
                        if (format == "json")
                        {
                            sources.FromJsonFile(
                                new JsonFileSourceOptions
                                {
                                    Path = path,
                                    WatchChanges = false,
                                    Transformers = transformers,
                                }
                            );
                        }
                        else
                        {
                            sources.FromYamlFile(
                                new YamlFileSourceOptions
                                {
                                    Path = path,
                                    WatchChanges = false,
                                    Transformers = transformers,
                                }
                            );
                        }
                    })
                );
            });
            var state = context.GetState<AppSettings>();
            await state.SaveAsync(patch => patch.Label = "secret");

            var raw = await File.ReadAllBytesAsync(path);
            Encoding
                .UTF8.GetString(raw)
                .StartsWith("guarded:", StringComparison.Ordinal)
                .ShouldBeTrue();
            (await state.GetValueAsync()).Label.ShouldBe("secret");
        }
    }

    [Test]
    public async Task CustomFileOptionsFixedIdAndReadOnlyKeepSemantics()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "settings.json");
        await WriteJsonFragmentAsync(
            path,
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(21) }
        );
        var fixedResourceId = new ResourceId("file-composition:fixed");
        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                    sources
                        .FromJsonFile(
                            new JsonFileSourceOptions
                            {
                                Path = path,
                                WatchChanges = false,
                                FixedResourceId = fixedResourceId,
                                ResourceOptions = new FileResourceOptions
                                {
                                    CreateBackup = false,
                                },
                            }
                        )
                        .Named("owned")
                )
            );
        });

        var state = context.GetRuntimeState<AppSettings>();
        (await state.GetValueAsync()).RetryCount.ShouldBe(21);
        var source = state.GetDiagnostics().Sources.Single();
        source.PhysicalOrigin.ShouldBe(Path.GetFullPath(path));
        source.FixedResourceId.ShouldBe(fixedResourceId);
        source.CanWrite.ShouldBeTrue();

        await context.GetState<AppSettings>().SaveAsync(patch => patch.Label = "owned-write");
        Directory
            .GetFiles(directory.FullPath, "*", SearchOption.TopDirectoryOnly)
            .Single()
            .ShouldBe(path);

        await using var readOnly = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                    sources.FromJsonFile(
                        new JsonFileSourceOptions
                        {
                            Path = path,
                            WatchChanges = false,
                            ReadOnly = true,
                        }
                    )
                )
            );
        });
        var readOnlyState = readOnly.GetRuntimeState<AppSettings>();
        (await readOnlyState.GetValueAsync()).Label.ShouldBe("owned-write");
        readOnlyState.GetDiagnostics().Sources.Single().CanWrite.ShouldBeFalse();
    }

    private static byte[] SerializeJson(AppSettings.Fragment fragment)
    {
        var output = new ArrayBufferWriter<byte>();
        new JsonStateCodec<AppSettings.Fragment>().Serialize(fragment, output, default);
        return output.WrittenMemory.ToArray();
    }

    private static byte[] SerializeXml(AppSettings.Fragment fragment)
    {
        var output = new ArrayBufferWriter<byte>();
        new XmlStateCodec<AppSettings.Fragment>().Serialize(fragment, output, default);
        return output.WrittenMemory.ToArray();
    }

    private static async Task WriteJsonFragmentAsync(string path, AppSettings.Fragment fragment)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, SerializeJson(fragment));
    }

    private static async Task WriteYamlFragmentAsync(string path, AppSettings.Fragment fragment)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var output = new ArrayBufferWriter<byte>();
        new YamlStateCodec<AppSettings.Fragment>(modelSchema: AppSettings.FragmentSchema).Serialize(
            fragment,
            output,
            default
        );
        await File.WriteAllBytesAsync(path, output.WrittenMemory.ToArray());
    }

    private static async Task WriteXmlFragmentAsync(string path, AppSettings.Fragment fragment)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, SerializeXml(fragment));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, Func<Task> poke)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!condition())
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                throw new TimeoutException("Timed out waiting for the watched file change.");
            }

            await poke();
        }
    }

    private sealed class PrefixTransformer(string prefix) : ISynchronousStateByteTransformer
    {
        public ReadOnlyMemory<byte> TransformRead(ReadOnlyMemory<byte> source)
        {
            var text = Encoding.UTF8.GetString(source.Span);
            text.StartsWith(prefix, StringComparison.Ordinal).ShouldBeTrue();
            return Encoding.UTF8.GetBytes(text.Substring(prefix.Length));
        }

        public ReadOnlyMemory<byte> TransformWrite(ReadOnlyMemory<byte> source) =>
            Encoding.UTF8.GetBytes(prefix + Encoding.UTF8.GetString(source.Span));
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
