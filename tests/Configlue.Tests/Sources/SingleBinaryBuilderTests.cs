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
                        model => model.StateName = "local",
                        storageKey: "database"
                    );
                    builder.Add<AppSettings>(model => model.StateName = "game", storageKey: "app");
                }
            )
        )
        {
            await context.GetState<AppSettings>().SaveAsync(settings => settings.RetryCount = 12);
            await context
                .GetState<AppSettings>("game")
                .SaveAsync(settings => settings.RetryCount = 24);
            await context
                .GetState<DatabaseSettings>("local")
                .SaveAsync(settings => settings.Port = 6543);
        }

        await using var reopened = CreateContext(
            path,
            builder =>
            {
                builder.Add<AppSettings>(storageKey: "app");
                builder.Add<DatabaseSettings>(
                    model => model.StateName = "local",
                    storageKey: "database"
                );
                builder.Add<AppSettings>(model => model.StateName = "game", storageKey: "app");
            }
        );

        (await reopened.GetState<AppSettings>().GetValueAsync()).RetryCount.ShouldBe(12);
        (await reopened.GetState<AppSettings>("game").GetValueAsync()).RetryCount.ShouldBe(24);
        (await reopened.GetState<DatabaseSettings>("local").GetValueAsync()).Port.ShouldBe(6543);

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
    public async Task SingleBinaryUnnamedAndNamedDefaultPersistIndependently()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "default-collision.bin");

        static void Configure(SingleBinaryBuilder binary)
        {
            binary.Add<AppSettings>(storageKey: "app");
            binary.Add<AppSettings>(model => model.StateName = "default", storageKey: "app");
            binary.Add<AppSettings>(model => model.StateName = "game", storageKey: "app");
        }

        await using (var context = CreateContext(path, Configure))
        {
            await context.GetState<AppSettings>().SaveAsync(settings => settings.RetryCount = 11);
            await context
                .GetState<AppSettings>("default")
                .SaveAsync(settings => settings.RetryCount = 22);
            await context
                .GetState<AppSettings>("game")
                .SaveAsync(settings => settings.RetryCount = 33);

            (await context.GetState<AppSettings>().GetValueAsync()).RetryCount.ShouldBe(11);
            (await context.GetState<AppSettings>("default").GetValueAsync()).RetryCount.ShouldBe(
                22
            );
            (await context.GetState<AppSettings>("game").GetValueAsync()).RetryCount.ShouldBe(33);
        }

        using (var stream = File.OpenRead(path))
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var entries = archive.Entries.Select(static entry => entry.FullName).ToHashSet();
            entries.ShouldContain("models/app/options/default.json");
            entries.ShouldContain("models/app/options/named/default.json");
            entries.ShouldContain("models/app/options/game.json");
        }

        await using (var reopened = CreateContext(path, Configure))
        {
            (await reopened.GetState<AppSettings>().GetValueAsync()).RetryCount.ShouldBe(11);
            (await reopened.GetState<AppSettings>("default").GetValueAsync()).RetryCount.ShouldBe(
                22
            );
            (await reopened.GetState<AppSettings>("game").GetValueAsync()).RetryCount.ShouldBe(33);

            // Updating the named default must leave the unnamed state untouched.
            await reopened
                .GetState<AppSettings>("default")
                .SaveAsync(settings => settings.RetryCount = 23);
            (await reopened.GetState<AppSettings>("default").GetValueAsync()).RetryCount.ShouldBe(
                23
            );
            (await reopened.GetState<AppSettings>().GetValueAsync()).RetryCount.ShouldBe(11);
        }

        await using var reread = CreateContext(path, Configure);
        (await reread.GetState<AppSettings>().GetValueAsync()).RetryCount.ShouldBe(11);
        (await reread.GetState<AppSettings>("default").GetValueAsync()).RetryCount.ShouldBe(23);
        (await reread.GetState<AppSettings>("game").GetValueAsync()).RetryCount.ShouldBe(33);
    }

    [Test]
    public async Task SingleBinaryExistingUnnamedEntryRemainsReadableAfterNamedDefaultSplit()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "legacy-unnamed.bin");

        // Simulate an archive written before the split: only the unnamed state exists.
        await using (var legacy = CreateContext(path, binary =>
            binary.Add<AppSettings>(storageKey: "app")
        ))
        {
            await legacy.GetState<AppSettings>().SaveAsync(settings => settings.RetryCount = 11);
        }

        static void Configure(SingleBinaryBuilder binary)
        {
            binary.Add<AppSettings>(storageKey: "app");
            binary.Add<AppSettings>(model => model.StateName = "default", storageKey: "app");
        }

        await using (var context = CreateContext(path, Configure))
        {
            // The pre-existing shared entry stays owned by the unnamed state.
            (await context.GetState<AppSettings>().GetValueAsync()).RetryCount.ShouldBe(11);
            // The named default starts from defaults instead of copying the unnamed value.
            (await context.GetState<AppSettings>("default").GetValueAsync()).RetryCount.ShouldBe(
                3
            );

            await context
                .GetState<AppSettings>("default")
                .SaveAsync(settings => settings.RetryCount = 22);
            (await context.GetState<AppSettings>().GetValueAsync()).RetryCount.ShouldBe(11);
            (await context.GetState<AppSettings>("default").GetValueAsync()).RetryCount.ShouldBe(
                22
            );
        }

        using (var stream = File.OpenRead(path))
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var entries = archive.Entries.Select(static entry => entry.FullName).ToHashSet();
            entries.ShouldContain("models/app/options/default.json");
            entries.ShouldContain("models/app/options/named/default.json");
        }

        await using var reread = CreateContext(path, Configure);
        (await reread.GetState<AppSettings>().GetValueAsync()).RetryCount.ShouldBe(11);
        (await reread.GetState<AppSettings>("default").GetValueAsync()).RetryCount.ShouldBe(22);
    }

    [Test]
    public async Task SingleBinarySlashStateNamesDoNotCollideWithNamedDefaultHierarchy()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "slash-names.bin");

        static void Configure(SingleBinaryBuilder binary)
        {
            binary.Add<AppSettings>(storageKey: "app");
            binary.Add<AppSettings>(model => model.StateName = "default", storageKey: "app");
            binary.Add<AppSettings>(model => model.StateName = "a/b", storageKey: "app");
            binary.Add<AppSettings>(model => model.StateName = "named", storageKey: "app");
            binary.Add<AppSettings>(
                model => model.StateName = "named/default",
                storageKey: "app"
            );
        }

        await using (var context = CreateContext(path, Configure))
        {
            await context.GetState<AppSettings>().SaveAsync(settings => settings.RetryCount = 11);
            await context
                .GetState<AppSettings>("default")
                .SaveAsync(settings => settings.RetryCount = 22);
            await context
                .GetState<AppSettings>("a/b")
                .SaveAsync(settings => settings.RetryCount = 33);
            await context
                .GetState<AppSettings>("named")
                .SaveAsync(settings => settings.RetryCount = 44);
            await context
                .GetState<AppSettings>("named/default")
                .SaveAsync(settings => settings.RetryCount = 55);
        }

        using (var stream = File.OpenRead(path))
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var entries = archive.Entries.Select(static entry => entry.FullName).ToHashSet();
            entries.ShouldContain("models/app/options/default.json");
            entries.ShouldContain("models/app/options/named/default.json");
            // '/' in state names is escaped, so these never clash with the named/ hierarchy.
            entries.ShouldContain("models/app/options/a%2Fb.json");
            entries.ShouldContain("models/app/options/named.json");
            entries.ShouldContain("models/app/options/named%2Fdefault.json");
        }

        await using var reopened = CreateContext(path, Configure);
        (await reopened.GetState<AppSettings>().GetValueAsync()).RetryCount.ShouldBe(11);
        (await reopened.GetState<AppSettings>("default").GetValueAsync()).RetryCount.ShouldBe(22);
        (await reopened.GetState<AppSettings>("a/b").GetValueAsync()).RetryCount.ShouldBe(33);
        (await reopened.GetState<AppSettings>("named").GetValueAsync()).RetryCount.ShouldBe(44);
        (await reopened.GetState<AppSettings>("named/default").GetValueAsync()).RetryCount.ShouldBe(
            55
        );
    }

    [Test]
    public async Task SingleBinaryUnnamedAndNamedDefaultAreScopedPerSubject()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "default-subjects.bin");
        var subject = new TestSubject("default-user");

        static void Configure(SingleBinaryBuilder binary)
        {
            binary.Add<AppSettings>(storageKey: "app");
            binary.Add<AppSettings>(model => model.StateName = "default", storageKey: "app");
        }

        await using (var context = CreateContext(path, Configure))
        {
            await context.GetState<AppSettings>().SaveAsync(settings => settings.RetryCount = 11);
            await context
                .GetState<AppSettings>("default")
                .SaveAsync(settings => settings.RetryCount = 22);

            var subjects = context.GetSubjectState<AppSettings>();
            var namedSubjects = context.GetSubjectState<AppSettings>("default");
            await subjects.ForSubject(subject).SaveAsync(settings => settings.RetryCount = 33);
            await namedSubjects.ForSubject(subject).SaveAsync(settings => settings.RetryCount = 44);

            (await subjects.ForSubject(subject).GetValueAsync()).RetryCount.ShouldBe(33);
            (await namedSubjects.ForSubject(subject).GetValueAsync()).RetryCount.ShouldBe(44);
            (await context.GetState<AppSettings>().GetValueAsync()).RetryCount.ShouldBe(11);
            (await context.GetState<AppSettings>("default").GetValueAsync()).RetryCount.ShouldBe(
                22
            );
        }

        using (var stream = File.OpenRead(path))
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var entries = archive.Entries.Select(static entry => entry.FullName).ToHashSet();
            entries.ShouldContain("models/app/options/default.json");
            entries.ShouldContain("models/app/options/named/default.json");
            entries.ShouldContain(SubjectEntryName(subject, "models/app/options/default.json"));
            entries.ShouldContain(
                SubjectEntryName(subject, "models/app/options/named/default.json")
            );
        }

        await using var reopened = CreateContext(path, Configure);
        (await reopened.GetState<AppSettings>().GetValueAsync()).RetryCount.ShouldBe(11);
        (await reopened.GetState<AppSettings>("default").GetValueAsync()).RetryCount.ShouldBe(22);
        var reopenedSubjects = reopened.GetSubjectState<AppSettings>();
        var reopenedNamedSubjects = reopened.GetSubjectState<AppSettings>("default");
        (await reopenedSubjects.ForSubject(subject).GetValueAsync()).RetryCount.ShouldBe(33);
        (await reopenedNamedSubjects.ForSubject(subject).GetValueAsync()).RetryCount.ShouldBe(44);
    }

    [Test]
    public async Task SingleBinaryProfilesRemainSeparateAfterNamedDefaultSplit()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "profiles-unaffected.bin");

        await using (var context = ConfiglueApp.CreateContext(configure =>
            configure.UseSingleBinary(binary =>
            {
                binary.WithLocal(path).WithProfiles();
                binary.Add<AppSettings>(storageKey: "app");
            })
        ))
        {
            await context.GetState<AppSettings>().SaveAsync(settings => settings.RetryCount = 11);

            var profiles = context.GetProfiledState<AppSettings>();
            var defaultProfile = await profiles.GetProfileAsync("default");
            await defaultProfile.SaveAsync(settings => settings.RetryCount = 7);
            await profiles.CreateProfileAsync("work", copyFrom: "default");
            var workProfile = await profiles.GetProfileAsync("work");
            await workProfile.SaveAsync(settings => settings.RetryCount = 19);
        }

        using (var stream = File.OpenRead(path))
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var entries = archive.Entries.Select(static entry => entry.FullName).ToHashSet();
            entries.ShouldContain("models/app/options/default.json");
            entries.ShouldContain("models/app/profiles/default.json");
            entries.ShouldContain("models/app/profiles/work.json");
        }

        await using var reopened = ConfiglueApp.CreateContext(configure =>
            configure.UseSingleBinary(binary =>
            {
                binary.WithLocal(path).WithProfiles();
                binary.Add<AppSettings>(storageKey: "app");
            })
        );
        var reopenedProfiles = reopened.GetProfiledState<AppSettings>();
        (await reopenedProfiles.GetProfileNamesAsync()).ShouldContain("default");
        (await reopened.GetState<AppSettings>().GetValueAsync()).RetryCount.ShouldBe(11);
        (await reopened.GetState<AppSettings>("default").GetValueAsync()).RetryCount.ShouldBe(7);
        (await reopened.GetState<AppSettings>("work").GetValueAsync()).RetryCount.ShouldBe(19);
    }

    [Test]
    public async Task SingleBinaryUnnamedAndNamedDefaultRoundTripWithEncryption()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "encrypted-default.bin");

        await using (var context = ConfiglueApp.CreateContext(configure =>
            configure.UseSingleBinary(binary =>
            {
                binary.WithLocal(path).WithPassphrase("default split secret");
                binary.Add<AppSettings>(storageKey: "app");
                binary.Add<AppSettings>(
                    model => model.StateName = "default",
                    storageKey: "app"
                );
            })
        ))
        {
            await context.GetState<AppSettings>().SaveAsync(settings => settings.RetryCount = 11);
            await context
                .GetState<AppSettings>("default")
                .SaveAsync(settings => settings.RetryCount = 22);
        }

        await using var reopened = ConfiglueApp.CreateContext(configure =>
            configure.UseSingleBinary(binary =>
            {
                binary.WithLocal(path).WithPassphrase("default split secret");
                binary.Add<AppSettings>(storageKey: "app");
                binary.Add<AppSettings>(
                    model => model.StateName = "default",
                    storageKey: "app"
                );
            })
        );
        (await reopened.GetState<AppSettings>().GetValueAsync()).RetryCount.ShouldBe(11);
        (await reopened.GetState<AppSettings>("default").GetValueAsync()).RetryCount.ShouldBe(22);
    }

    [Test]
    public async Task SingleBinaryUsesTheSelectedHostProfileForUserGlobalPaths()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "user", "save.bin");

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.OverrideHostPath(
                ConfiglueStandardLocation.UserGlobal,
                _ => Path.GetDirectoryName(path)!
            );
            builder.UseSingleBinary(binary =>
            {
                binary.WithUserGlobal("Example", "save.bin");
                binary.Add<AppSettings>(storageKey: "app");
            });
        });

        await context.GetState<AppSettings>().SaveAsync(settings => settings.RetryCount = 6);

        File.Exists(path).ShouldBeTrue();
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
            var profiles = context.GetProfiledState<AppSettings>();
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
        var reopenedProfiles = reopened.GetProfiledState<AppSettings>();
        (await reopenedProfiles.GetProfileNamesAsync()).ShouldContain("work");
        (await reopenedProfiles.GetActiveProfileNameAsync()).ShouldBe("work");
        (await reopened.GetState<AppSettings>("default").GetValueAsync()).RetryCount.ShouldBe(7);
        (await reopened.GetState<AppSettings>("work").GetValueAsync()).RetryCount.ShouldBe(19);
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
            await context.GetState<AppSettings>().SaveAsync(settings => settings.RetryCount = 42);
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
        (await reopened.GetState<AppSettings>().GetValueAsync()).RetryCount.ShouldBe(42);
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
            await context.GetState<AppSettings>().SaveAsync(settings => settings.RetryCount = 53);
        }

        await using var reopened = ConfiglueApp.CreateContext(configure =>
            configure.UseSingleBinary(binary =>
            {
                binary.WithLocal(path).WithAesKey(key);
                binary.Add<AppSettings>(storageKey: "app");
            })
        );
        (await reopened.GetState<AppSettings>().GetValueAsync()).RetryCount.ShouldBe(53);
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
            await context.GetState<AppSettings>().SaveAsync(settings => settings.RetryCount = 54);
        }

        await using var reopened = ConfiglueApp.CreateContext(configure =>
            configure.UseSingleBinary(binary =>
            {
                binary.WithLocal(path).WithEncryption(transformer);
                binary.Add<AppSettings>(storageKey: "app");
            })
        );
        (await reopened.GetState<AppSettings>().GetValueAsync()).RetryCount.ShouldBe(54);
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

        var app = context.GetRuntimeState<AppSettings>();
        var database = context.GetRuntimeState<DatabaseSettings>();
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

    [Test]
    public async Task SingleBinaryStoresEachSubjectInItsOwnEntryAcrossContexts()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "subjects.bin");
        var subjectA = new TestSubject("user/a");
        var subjectB = new TestSubject("user%a");

        await using (
            var context = ConfiglueApp.CreateContext(configure =>
                configure.UseSingleBinary(binary =>
                {
                    binary.WithLocal(path);
                    binary.Add<AppSettings>(storageKey: "app");
                })
            )
        )
        {
            await context.GetState<AppSettings>().SaveAsync(settings => settings.RetryCount = 1);
            var subjects = context.GetSubjectState<AppSettings>();
            var stateA = subjects.ForSubject(subjectA);
            var stateB = subjects.ForSubject(subjectB);
            await Task.WhenAll(
                stateA.SaveAsync(settings => settings.RetryCount = 2).AsTask(),
                stateB.SaveAsync(settings => settings.RetryCount = 3).AsTask()
            );

            (await stateA.GetValueAsync()).RetryCount.ShouldBe(2);
            (await stateB.GetValueAsync()).RetryCount.ShouldBe(3);
        }

        await using var reopened = ConfiglueApp.CreateContext(configure =>
            configure.UseSingleBinary(binary =>
            {
                binary.WithLocal(path);
                binary.Add<AppSettings>(storageKey: "app");
            })
        );
        var reopenedSubjects = reopened.GetSubjectState<AppSettings>();
        (await reopened.GetState<AppSettings>().GetValueAsync()).RetryCount.ShouldBe(1);
        (await reopenedSubjects.ForSubject(subjectA).GetValueAsync()).RetryCount.ShouldBe(2);
        (await reopenedSubjects.ForSubject(subjectB).GetValueAsync()).RetryCount.ShouldBe(3);

        using var stream = File.OpenRead(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var entries = archive.Entries.Select(static entry => entry.FullName).ToHashSet();
        entries.ShouldContain("models/app/options/default.json");
        entries.ShouldContain(SubjectEntryName(subjectA, "models/app/options/default.json"));
        entries.ShouldContain(SubjectEntryName(subjectB, "models/app/options/default.json"));
    }

    [Test]
    public async Task SingleBinaryNamedStateEntriesAreScopedToTheirSubject()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "named-subjects.bin");
        var subject = new TestSubject("named-user");

        await using (
            var context = ConfiglueApp.CreateContext(configure =>
                configure.UseSingleBinary(binary =>
                {
                    binary.WithLocal(path);
                    binary.Add<AppSettings>(storageKey: "app");
                    binary.Add<AppSettings>(model => model.StateName = "game", storageKey: "app");
                })
            )
        )
        {
            var subjectStates = context.GetSubjectState<AppSettings>();
            await subjectStates.ForSubject(subject).SaveAsync(settings => settings.RetryCount = 4);
            await context
                .GetSubjectState<AppSettings>("game")
                .ForSubject(subject)
                .SaveAsync(settings => settings.RetryCount = 5);

            (
                await context
                    .GetSubjectState<AppSettings>("game")
                    .ForSubject(subject)
                    .GetValueAsync()
            ).RetryCount.ShouldBe(5);
        }

        using var stream = File.OpenRead(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var entries = archive.Entries.Select(static entry => entry.FullName).ToHashSet();
        entries.ShouldContain(SubjectEntryName(subject, "models/app/options/default.json"));
        entries.ShouldContain(SubjectEntryName(subject, "models/app/options/game.json"));
    }

    [Test]
    public async Task SingleBinaryEncryptedArchivePreservesMultipleSubjects()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.FullPath, "encrypted-subjects.bin");
        var subjectA = new TestSubject("encrypted-a");
        var subjectB = new TestSubject("encrypted-b");

        await using (
            var context = ConfiglueApp.CreateContext(configure =>
                configure.UseSingleBinary(binary =>
                {
                    binary.WithLocal(path).WithPassphrase("subject archive secret");
                    binary.Add<AppSettings>(storageKey: "app");
                })
            )
        )
        {
            var subjects = context.GetSubjectState<AppSettings>();
            await Task.WhenAll(
                subjects
                    .ForSubject(subjectA)
                    .SaveAsync(settings => settings.RetryCount = 10)
                    .AsTask(),
                subjects
                    .ForSubject(subjectB)
                    .SaveAsync(settings => settings.RetryCount = 20)
                    .AsTask()
            );
        }

        await using var reopened = ConfiglueApp.CreateContext(configure =>
            configure.UseSingleBinary(binary =>
            {
                binary.WithLocal(path).WithPassphrase("subject archive secret");
                binary.Add<AppSettings>(storageKey: "app");
            })
        );
        var reopenedSubjects = reopened.GetSubjectState<AppSettings>();
        (await reopenedSubjects.ForSubject(subjectA).GetValueAsync()).RetryCount.ShouldBe(10);
        (await reopenedSubjects.ForSubject(subjectB).GetValueAsync()).RetryCount.ShouldBe(20);
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

    private static string SubjectEntryName(TestSubject subject, string entryName) =>
        $"subjects/{Uri.EscapeDataString(subject.Key.Value)}/{entryName}";

    private sealed record TestSubject(string Id) : IConfiglueSubject
    {
        public SubjectKey Key => SubjectKey.FromSegments(Id);
    }

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
