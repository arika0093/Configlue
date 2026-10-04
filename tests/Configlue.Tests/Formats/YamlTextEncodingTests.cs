using System.Buffers;
using System.Text;
using Configlue.Provider.Yaml;
using Configlue.Testing;

namespace Configlue.Tests;

/// <summary>Regression tests for issue #181: YAML textEncoding must be honored consistently.</summary>
public sealed class YamlTextEncodingTests
{
    private const string NonAsciiLabel = "héllo-日本語";

    private static Encoding[] Encodings() =>
    [
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
        Encoding.Unicode,
        Encoding.BigEndianUnicode,
    ];

    private static DocumentLayoutOptions[] Layouts() =>
    [
        new DocumentLayoutOptions { Layout = DocumentLayout.Simple },
        new DocumentLayoutOptions { Layout = DocumentLayout.Detailed },
    ];

    [Test]
    public async Task YamlCodec_SerializeDeserialize_RoundtripsInConfiguredEncoding()
    {
        foreach (var encoding in Encodings())
        {
            foreach (var layout in Layouts())
            {
                // Typed fast path.
                var typed = new YamlStateCodec<AppSettings.Fragment>(
                    modelSchema: AppSettings.FragmentSchema,
                    documentLayout: layout,
                    textEncoding: encoding
                );
                var fragment = new AppSettings.Fragment
                {
                    RetryCount = Optional<int>.Present(7),
                    Label = Optional<string?>.Present(NonAsciiLabel),
                };
                var typedBuffer = new ArrayBufferWriter<byte>();
                var context = new StateCodecContext(new StateSchemaMetadata("app-settings", 2));
                typed.Serialize(fragment, typedBuffer, in context);
                var typedBytes = typedBuffer.WrittenMemory.ToArray();

                // Serialize must emit the configured encoding so the text decodes losslessly.
                encoding.GetString(typedBytes).ShouldContain(NonAsciiLabel);
                var typedSequence = new ReadOnlySequence<byte>(typedBytes);
                var typedDecoded = typed.Deserialize(in typedSequence, default)!;
                typedDecoded.RetryCount.Value.ShouldBe(7);
                typedDecoded.Label.Value.ShouldBe(NonAsciiLabel);

                // Untyped delegation path.
                var untyped = new YamlStateCodec(
                    modelSchema: AppSettings.FragmentSchema,
                    documentLayout: layout,
                    textEncoding: encoding
                );
                var untypedBuffer = new ArrayBufferWriter<byte>();
                untyped.Serialize(typeof(AppSettings.Fragment), fragment, untypedBuffer, in context);
                var untypedBytes = untypedBuffer.WrittenMemory.ToArray();
                encoding.GetString(untypedBytes).ShouldContain(NonAsciiLabel);
                var untypedSequence = new ReadOnlySequence<byte>(untypedBytes);
                var untypedDecoded =
                    (AppSettings.Fragment)untyped.Deserialize(typeof(AppSettings.Fragment), in untypedSequence, default)!;
                untypedDecoded.RetryCount.Value.ShouldBe(7);
                untypedDecoded.Label.Value.ShouldBe(NonAsciiLabel);
            }
        }
    }

    [Test]
    public async Task YamlCodec_WithoutEncoding_KeepsUtf8Behavior()
    {
        var codec = new YamlStateCodec<AppSettings.Fragment>(
            modelSchema: AppSettings.FragmentSchema
        );
        var fragment = new AppSettings.Fragment
        {
            RetryCount = Optional<int>.Present(7),
            Label = Optional<string?>.Present(NonAsciiLabel),
        };
        var buffer = new ArrayBufferWriter<byte>();
        var context = new StateCodecContext(new StateSchemaMetadata("app-settings", 2));
        codec.Serialize(fragment, buffer, in context);
        var bytes = buffer.WrittenMemory.ToArray();

        // Unspecified encoding keeps the legacy strict UTF-8 behavior.
        var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        strictUtf8.GetString(bytes).ShouldContain(NonAsciiLabel);
        var sequence = new ReadOnlySequence<byte>(bytes);
        var decoded = codec.Deserialize(in sequence, default)!;
        decoded.RetryCount.Value.ShouldBe(7);
        decoded.Label.Value.ShouldBe(NonAsciiLabel);
    }

    [Test]
    public async Task YamlSection_Update_PreservesBomCommentsAndSiblings_InUtf16()
    {
        foreach (var encoding in new Encoding[] { Encoding.Unicode, Encoding.BigEndianUnicode })
        {
            var yaml =
                "# Keep the root comment.\n"
                + "App:\n"
                + "  # Keep the section comment.\n"
                + "  Settings:\n"
                + "    $version: 2\n"
                + "    RetryCount: 4 # Keep the inline comment.\n"
                + "  Other:\n"
                + "    Value: 保持-nested\n"
                + "OtherSection:\n"
                + "  Value: présérvé-root\n"
                + "# Keep the trailing comment.\n";
            var preamble = encoding.GetPreamble();
            var original = preamble.Concat(encoding.GetBytes(yaml)).ToArray();

            var resource = new InMemoryResource();
            await resource.WriteAsync(new ResourceWriteRequest(original));
            var section = new YamlSectionResource(
                resource,
                resource,
                "App:Settings",
                textEncoding: encoding
            );
            var codec = new YamlStateCodec<AppSettings.Fragment>(
                modelSchema: AppSettings.FragmentSchema,
                textEncoding: encoding
            );
            var source = new StateSource<AppSettings.Fragment>("settings", new SerializedSource<AppSettings.Fragment>(section, codec, writer: (IResourceReader)section as IResourceWriter, watcher: (IResourceReader)section as ISourceWatcher), new StateSourceOptions<AppSettings.Fragment>());
            await using var options = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
                new StateSourceSet<AppSettings.Fragment>([source])
            );

            await options.SaveAsync(
                new AppSettings.Patch
                {
                    RetryCount = FragmentOperation<int>.Set(9),
                    Label = FragmentOperation<string?>.Set(NonAsciiLabel),
                }
            );

            var updated = (await resource.ReadAsync()).Content.ToArray();
            updated.AsSpan(0, preamble.Length).ToArray().ShouldBe(preamble);
            var text = encoding.GetString(updated.AsSpan(preamble.Length));
            text.ShouldContain("# Keep the root comment.");
            text.ShouldContain("# Keep the section comment.");
            text.ShouldContain("# Keep the inline comment.");
            text.ShouldContain("# Keep the trailing comment.");
            text.ShouldContain("保持-nested");
            text.ShouldContain("présérvé-root");
            text.ShouldContain("RetryCount: 9");
            text.ShouldContain(NonAsciiLabel);

            // The updated document must still read back through the configured encoding.
            var reread = await source.Reader.ReadAsync();
            reread.Status.ShouldBe(StateReadStatus.Success);
            reread.Value!.RetryCount.Value.ShouldBe(9);
            reread.Value.Label.Value.ShouldBe(NonAsciiLabel);
        }
    }
}
