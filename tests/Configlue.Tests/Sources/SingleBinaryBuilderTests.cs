using System.IO.Compression;
using Configlue.Source.Presets;
using Configlue.Transformer.AES;

namespace Configlue.Tests;

public sealed class SingleBinaryBuilderTests
{
    [Test]
    public async Task SingleBinaryStoresMultipleModelsAndNamedOptionsInOneArchive()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "savedata.bin");

        await using (
            var context = CreateContext(
                path,
                builder =>
                {
                    builder.Add<AppSettings>(storageKey: "app");
                    builder.Add<DatabaseSettings>(
                        model => model.OptionsName = "local",
                        storageKey: "database"
                    );
                    builder.Add<AppSettings>(
                        model => model.OptionsName = "game",
                        storageKey: "app"
                    );
                }
            )
        )
        {
            await context.GetOptions<AppSettings>().SaveAsync(settings => settings.RetryCount = 12);
            await context
                .GetOptions<AppSettings>("game")
                .SaveAsync(settings => settings.RetryCount = 24);
            await context
                .GetOptions<DatabaseSettings>("local")
                .SaveAsync(settings => settings.Port = 6543);
        }

        await using var reopened = CreateContext(
            path,
            builder =>
            {
                builder.Add<AppSettings>(storageKey: "app");
                builder.Add<DatabaseSettings>(
                    model => model.OptionsName = "local",
                    storageKey: "database"
                );
                builder.Add<AppSettings>(model => model.OptionsName = "game", storageKey: "app");
            }
        );

        (await reopened.GetOptions<AppSettings>().GetValueAsync()).RetryCount.ShouldBe(12);
        (await reopened.GetOptions<AppSettings>("game").GetValueAsync()).RetryCount.ShouldBe(24);
        (await reopened.GetOptions<DatabaseSettings>("local").GetValueAsync()).Port.ShouldBe(6543);

        using var stream = File.OpenRead(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        archive
            .Entries.Select(static entry => entry.FullName)
            .Order(StringComparer.Ordinal)
            .ShouldBe(
                new[]
                {
                    "models/app/options/default.json",
                    "models/app/options/game.json",
                    "models/database/options/local.json",
                }.Order(StringComparer.Ordinal)
            );
    }

    [Test]
    public async Task SingleBinaryProfilesPersistTheirCatalogAndSeparateValues()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "profiles.bin");

        await using (
            var context = ConfiglueApp.CreateContext(configure =>
                configure.UseSingleBinary(binary =>
                {
                    binary.WithLocal(path).WithProfiles();
                    binary.Add<AppSettings>(storageKey: "app");
                })
            )
        )
        {
            var profiles = context.GetProfiledOptions<AppSettings>();
            (await profiles.GetProfileNamesAsync()).ShouldContain("default");
            var defaultProfile = await profiles.GetProfileAsync("default");
            await defaultProfile.SaveAsync(settings => settings.RetryCount = 7);
            await profiles.CreateProfileAsync("work", copyFrom: "default");
            var workProfile = await profiles.GetProfileAsync("work");
            await workProfile.SaveAsync(settings => settings.RetryCount = 19);
            await profiles.SetActiveProfileAsync("work");
        }

        await using var reopened = ConfiglueApp.CreateContext(configure =>
            configure.UseSingleBinary(binary =>
            {
                binary.WithLocal(path).WithProfiles();
                binary.Add<AppSettings>(storageKey: "app");
            })
        );
        var reopenedProfiles = reopened.GetProfiledOptions<AppSettings>();
        (await reopenedProfiles.GetProfileNamesAsync()).ShouldContain("work");
        (await reopenedProfiles.GetActiveProfileNameAsync()).ShouldBe("work");
        (await reopened.GetOptions<AppSettings>("default").GetValueAsync()).RetryCount.ShouldBe(7);
        (await reopened.GetOptions<AppSettings>("work").GetValueAsync()).RetryCount.ShouldBe(19);
    }

    [Test]
    public async Task SingleBinaryPassphraseEncryptionRoundTripsAcrossContexts()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "encrypted.bin");

        await using (
            var context = ConfiglueApp.CreateContext(configure =>
                configure.UseSingleBinary(binary =>
                {
                    binary.WithLocal(path).WithEncrypted("correct horse battery staple");
                    binary.Add<AppSettings>(storageKey: "app");
                })
            )
        )
        {
            await context.GetOptions<AppSettings>().SaveAsync(settings => settings.RetryCount = 42);
        }

        var encrypted = await File.ReadAllBytesAsync(path);
        encrypted.AsSpan().StartsWith("PK"u8).ShouldBeFalse();

        await using var reopened = ConfiglueApp.CreateContext(configure =>
            configure.UseSingleBinary(binary =>
            {
                binary.WithLocal(path).WithPassphrase("correct horse battery staple");
                binary.Add<AppSettings>(storageKey: "app");
            })
        );
        (await reopened.GetOptions<AppSettings>().GetValueAsync()).RetryCount.ShouldBe(42);
    }

    [Test]
    public async Task SingleBinaryAesKeyOptionRoundTripsAcrossContexts()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "aes.bin");
        var key = new byte[32];

        await using (
            var context = ConfiglueApp.CreateContext(configure =>
                configure.UseSingleBinary(binary =>
                {
                    binary.WithLocal(path).WithAesKey(key);
                    binary.Add<AppSettings>(storageKey: "app");
                })
            )
        )
        {
            await context.GetOptions<AppSettings>().SaveAsync(settings => settings.RetryCount = 53);
        }

        await using var reopened = ConfiglueApp.CreateContext(configure =>
            configure.UseSingleBinary(binary =>
            {
                binary.WithLocal(path).WithAesKey(key);
                binary.Add<AppSettings>(storageKey: "app");
            })
        );
        (await reopened.GetOptions<AppSettings>().GetValueAsync()).RetryCount.ShouldBe(53);
    }

    [Test]
    public async Task SingleBinaryAcceptsACallerOwnedTransformer()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "custom-transformer.bin");
        using var transformer = new AesGcmStateByteTransformer(new byte[32]);

        await using (
            var context = ConfiglueApp.CreateContext(configure =>
                configure.UseSingleBinary(binary =>
                {
                    binary.WithLocal(path).WithEncryption(transformer);
                    binary.Add<AppSettings>(storageKey: "app");
                })
            )
        )
        {
            await context.GetOptions<AppSettings>().SaveAsync(settings => settings.RetryCount = 54);
        }

        await using var reopened = ConfiglueApp.CreateContext(configure =>
            configure.UseSingleBinary(binary =>
            {
                binary.WithLocal(path).WithEncryption(transformer);
                binary.Add<AppSettings>(storageKey: "app");
            })
        );
        (await reopened.GetOptions<AppSettings>().GetValueAsync()).RetryCount.ShouldBe(54);
    }

    [Test]
    public async Task ConcurrentWritesToDifferentEntriesMergeAndSameEntryConflicts()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "concurrent.bin");
        await using var context = ConfiglueApp.CreateContext(configure =>
            configure.UseSingleBinary(binary =>
            {
                binary.WithLocal(path);
                binary.Add<AppSettings>(storageKey: "app");
                binary.Add<DatabaseSettings>(storageKey: "database");
            })
        );

        var app = context.GetRuntimeOptions<AppSettings>();
        var database = context.GetRuntimeOptions<DatabaseSettings>();
        await app.SaveAsync(settings => settings.RetryCount = 60);
        using var appEdit = await app.OpenEditSessionAsync();
        using var databaseEdit = await database.OpenEditSessionAsync();
        appEdit.Value!.RetryCount = 61;
        databaseEdit.Value!.Port = 7061;
        await Task.WhenAll(appEdit.CommitAsync().AsTask(), databaseEdit.CommitAsync().AsTask());

        (await app.GetValueAsync()).RetryCount.ShouldBe(61);
        (await database.GetValueAsync()).Port.ShouldBe(7061);

        using var first = await app.OpenEditSessionAsync();
        using var second = await app.OpenEditSessionAsync();
        first.Value!.RetryCount = 62;
        second.Value!.RetryCount = 63;
        var commits = await Task.WhenAll(Commit(first), Commit(second));
        commits.Count(static succeeded => succeeded).ShouldBe(1);
        new[] { 62, 63 }.ShouldContain((await app.GetValueAsync()).RetryCount);
    }

    private static async Task<bool> Commit(EditSession<AppSettings> session)
    {
        try
        {
            await session.CommitAsync();
            return true;
        }
        catch (StateConflictException)
        {
            return false;
        }
    }

    private static ConfiglueContext CreateContext(
        string path,
        Action<SingleBinaryBuilder> configure
    ) =>
        ConfiglueApp.CreateContext(builder =>
            builder.UseSingleBinary(binary =>
            {
                binary.WithLocal(path);
                configure(binary);
            })
        );

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory() =>
            FullPath = Path.Combine(
                Path.GetTempPath(),
                "Configlue.Tests",
                Guid.NewGuid().ToString("N")
            );

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
