using System.Buffers;
using System.Text;
using Configlue.Provider.MessagePack;
using MessagePack;

namespace Configlue.Tests;

/// <summary>
/// Covers MessagePack file read/write/watch paths through the standard-layer
/// file composition (#260), including the generated-codec AOT path, custom file
/// options, transformers, fixed IDs, and read-only sources.
/// </summary>
public sealed class MessagePackFileSourceCompositionTests
{
    [Test]
    public async Task MessagePackFile_ReadWriteWatchThroughStandardComposition()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "settings.msgpack");
        await WriteFragmentAsync(
            path,
            MessagePackSampleSettings.Fragment.From(
                new MessagePackSampleSettings { Name = "file", Count = 7 }
            )
        );

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<MessagePackSampleSettings>(model =>
                model.Sources(sources =>
                    sources.FromMessagePackFile(
                        new MessagePackFileSourceOptions
                        {
                            Path = path,
                            WatchChanges = false,
                            SerializerOptions = TestMessagePack.Options,
                        }
                    )
                )
            );
        });
        var state = context.GetState<MessagePackSampleSettings>();
        (await state.GetValueAsync()).Count.ShouldBe(7);

        await state.SaveAsync(patch => patch.Name = "saved-msgpack");
        (await state.GetValueAsync()).Name.ShouldBe("saved-msgpack");

        await using var watcher = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<MessagePackSampleSettings>(model =>
                model.Sources(sources =>
                    sources.FromMessagePackFile(
                        new MessagePackFileSourceOptions
                        {
                            Path = path,
                            SerializerOptions = TestMessagePack.Options,
                        }
                    )
                )
            );
        });
        await using var writer = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<MessagePackSampleSettings>(model =>
                model.Sources(sources =>
                    sources.FromMessagePackFile(
                        new MessagePackFileSourceOptions
                        {
                            Path = path,
                            WatchChanges = false,
                            SerializerOptions = TestMessagePack.Options,
                        }
                    )
                )
            );
        });
        var observed = string.Empty;
        var observedGate = new object();
        using var subscription = watcher
            .GetState<MessagePackSampleSettings>()
            .OnChange(changed =>
            {
                lock (observedGate)
                {
                    observed = changed.Name;
                }
            });
        await writer
            .GetState<MessagePackSampleSettings>()
            .SaveAsync(patch => patch.Name = "watched-msgpack");
        await WaitUntilAsync(
            () =>
            {
                lock (observedGate)
                {
                    return observed == "watched-msgpack";
                }
            },
            async () =>
                await writer
                    .GetState<MessagePackSampleSettings>()
                    .SaveAsync(patch => patch.Name = "watched-msgpack")
        );
    }

    [Test]
    public async Task GeneratedMessagePackFile_ReadWriteThroughStandardComposition()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "generated.msgpack");

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<MessagePackSampleSettings>(model =>
                model.Sources(sources =>
                    sources.FromGeneratedMessagePackFile(
                        new MessagePackFileSourceOptions { Path = path, WatchChanges = false },
                        TestMessagePack.Options
                    )
                )
            );
        });
        var state = context.GetState<MessagePackSampleSettings>();
        (await state.GetValueAsync()).Name.ShouldBe(string.Empty);

        await state.SaveAsync(patch => patch.Name = "generated");
        (await state.GetValueAsync()).Name.ShouldBe("generated");

        await using var restarted = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<MessagePackSampleSettings>(model =>
                model.Sources(sources =>
                    sources.FromGeneratedMessagePackFile(
                        new MessagePackFileSourceOptions { Path = path, WatchChanges = false },
                        TestMessagePack.Options
                    )
                )
            );
        });
        (await restarted.GetState<MessagePackSampleSettings>().GetValueAsync()).Name.ShouldBe(
            "generated"
        );
    }

    [Test]
    public async Task CustomFileOptionsTransformerFixedIdAndReadOnlyKeepSemantics()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "settings.msgpack");
        var fixedResourceId = new ResourceId("messagepack:fixed");

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<MessagePackSampleSettings>(model =>
                model.Sources(sources =>
                    sources
                        .FromMessagePackFile(
                            new MessagePackFileSourceOptions
                            {
                                Path = path,
                                WatchChanges = false,
                                SerializerOptions = TestMessagePack.Options,
                                FixedResourceId = fixedResourceId,
                                Transformers = new IStateByteTransformer[]
                                {
                                    new PrefixTransformer("guarded:"u8.ToArray()),
                                },
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

        var state = context.GetRuntimeState<MessagePackSampleSettings>();
        (await state.GetValueAsync()).Count.ShouldBe(0);
        var source = state.GetDiagnostics().Sources.Single();
        source.PhysicalOrigin.ShouldBe(Path.GetFullPath(path));
        source.FixedResourceId.ShouldBe(fixedResourceId);
        source.CanWrite.ShouldBeTrue();

        await context
            .GetState<MessagePackSampleSettings>()
            .SaveAsync(patch => patch.Name = "owned-write");
        (await state.GetValueAsync()).Name.ShouldBe("owned-write");
        var raw = await File.ReadAllBytesAsync(path);
        raw.AsSpan(0, "guarded:".Length).ToArray().ShouldBe("guarded:"u8.ToArray());

        await using var readOnly = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<MessagePackSampleSettings>(model =>
                model.Sources(sources =>
                    sources.FromMessagePackFile(
                        new MessagePackFileSourceOptions
                        {
                            Path = path,
                            WatchChanges = false,
                            ReadOnly = true,
                            SerializerOptions = TestMessagePack.Options,
                            Transformers = new IStateByteTransformer[]
                            {
                                new PrefixTransformer("guarded:"u8.ToArray()),
                            },
                        }
                    )
                )
            );
        });
        var readOnlyState = readOnly.GetRuntimeState<MessagePackSampleSettings>();
        (await readOnlyState.GetValueAsync()).Name.ShouldBe("owned-write");
        readOnlyState.GetDiagnostics().Sources.Single().CanWrite.ShouldBeFalse();
    }

    private static async Task WriteFragmentAsync(
        string path,
        MessagePackSampleSettings.Fragment fragment
    )
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var output = new ArrayBufferWriter<byte>();
        new MessagePackStateCodec<MessagePackSampleSettings.Fragment>(
            TestMessagePack.Options
        ).Serialize(fragment, output, default);
        await File.WriteAllBytesAsync(path, output.WrittenMemory.ToArray());
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

    private sealed class PrefixTransformer(byte[] prefix) : ISynchronousStateByteTransformer
    {
        public ReadOnlyMemory<byte> TransformRead(ReadOnlyMemory<byte> source)
        {
            var span = source.Span;
            span.Length.ShouldBeGreaterThan(prefix.Length);
            span.Slice(0, prefix.Length).ToArray().ShouldBe(prefix);
            return source.Slice(prefix.Length).ToArray();
        }

        public ReadOnlyMemory<byte> TransformWrite(ReadOnlyMemory<byte> source)
        {
            var output = new byte[prefix.Length + source.Length];
            prefix.CopyTo(output, 0);
            source.Span.CopyTo(output.AsSpan(prefix.Length));
            return output;
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
