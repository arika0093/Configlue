using System.Buffers;
using Configlue.Provider.Json;
using Configlue.Source.Common;

namespace Configlue.Tests;

public sealed class CommonSourcePresetContractTests
{
    [Test]
    public async Task UseCommonSourcesConvenienceOverload_LoadsTheSelectedFile()
    {
        using var directory = new TemporaryDirectory();
        var applicationId = $"Configlue.Tests.{Guid.NewGuid():N}";
        var selectedPath = Path.Combine(directory.Path, "selected.json");
        var output = new ArrayBufferWriter<byte>();
        new JsonStateCodec<AppSettings.Fragment>().Serialize(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(29) },
            output,
            default
        );
        await File.WriteAllBytesAsync(selectedPath, output.WrittenMemory.ToArray());

        await using var context = Configlue.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model => model.UseCommonSources(applicationId, selectedPath));
        });

        (await context.GetOptions<AppSettings>().GetValueAsync()).RetryCount.ShouldBe(29);
    }

    [Test]
    public void UseCommonSources_RejectsSpecificWriteLayerWithoutSpecificPath()
    {
        var model = new ConfiglueModelBuilder<AppSettings>();

        Should.Throw<InvalidOperationException>(() =>
            model.UseCommonSources(
                new CommonSourceOptions
                {
                    ApplicationId = $"Configlue.Tests.{Guid.NewGuid():N}",
                    GlobalFileName = "settings.json",
                    WriteLayer = CommonSourceWriteLayer.Specific,
                }
            )
        );
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "Configlue.Tests",
                Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
