using Configlue.Provider.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Configlue.Tests;

public sealed class ProfiledOptionsTests
{
    [Test]
    public async Task ProfileCatalog_PersistsProfilesValuesAndActiveSelectionAcrossServiceProviders()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "Configlue.Tests",
            Guid.NewGuid().ToString("N")
        );
        var filePath = Path.Combine(directory, "profiles.json");
        Directory.CreateDirectory(directory);
        try
        {
            using (var firstProvider = CreateServiceProvider(filePath))
            {
                var profiles = firstProvider.GetRequiredService<
                    IConfiglueProfiledOptions<AppSettings>
                >();
                var initialNames = await profiles.GetProfileNamesAsync();

                (profiles.DefaultProfileName).ShouldBe("default");
                ((initialNames))
                    .OrderBy(static item => item)
                    .ShouldBe((new[] { "default" }).OrderBy(static item => item));
                (await profiles.GetActiveProfileNameAsync()).ShouldBe("default");

                var defaultProfile = await profiles.GetActiveProfileAsync();
                await defaultProfile.SaveAsync(new AppSettings { RetryCount = 4, Label = "Light" });
                await profiles.CreateProfileAsync("Work", copyFrom: "default");
                await profiles.SetActiveProfileAsync("Work");
                ((await profiles.GetActiveValueAsync()).Label).ShouldBe("Light");

                var workProfile = await profiles.GetActiveProfileAsync();
                await workProfile.SaveAsync(new AppSettings { RetryCount = 9, Label = "Dark" });
                var defaultValue = await (
                    await profiles.GetProfileAsync("default")
                ).GetValueAsync();

                (defaultValue.Label).ShouldBe("Light");
                ((await profiles.GetActiveValueAsync()).Label).ShouldBe("Dark");
            }

            using (var restartedProvider = CreateServiceProvider(filePath))
            {
                var profiles = restartedProvider.GetRequiredService<
                    IConfiglueProfiledOptions<AppSettings>
                >();
                var restoredNames = await profiles.GetProfileNamesAsync();

                ((restoredNames))
                    .OrderBy(static item => item)
                    .ShouldBe((new[] { "default", "Work" }).OrderBy(static item => item));
                (await profiles.GetActiveProfileNameAsync()).ShouldBe("Work");
                ((await profiles.GetActiveValueAsync()).Label).ShouldBe("Dark");
                (
                    restartedProvider
                        .GetRequiredService<IOptionsMonitor<AppSettings>>()
                        .Get("Work")
                        .Label
                ).ShouldBe("Dark");

                var activeChanged = new TaskCompletionSource<string>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                );
                profiles.ActiveProfileChanged += profileName =>
                    activeChanged.TrySetResult(profileName);
                await profiles.RemoveProfileAsync("Work");

                (await activeChanged.Task.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe("default");
                ((await profiles.GetProfileNamesAsync()))
                    .OrderBy(static item => item)
                    .ShouldBe((new[] { "default" }).OrderBy(static item => item));
                (await profiles.GetActiveProfileNameAsync()).ShouldBe("default");
                ((await profiles.GetActiveValueAsync()).Label).ShouldBe("Light");
            }

            var document = await File.ReadAllTextAsync(filePath);
            (document).ShouldContain("ProfileCatalog");
            (document).ShouldContain("default");
            (document).ShouldContain("Work");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Test]
    public async Task ProfileCatalog_ValidatesNamesAndProtectsDefaultProfile()
    {
        using var directory = new TemporaryDirectory();
        using var serviceProvider = CreateServiceProvider(
            Path.Combine(directory.FullPath, "profiles.json")
        );
        var profiles = serviceProvider.GetRequiredService<IConfiglueProfiledOptions<AppSettings>>();
        await profiles.GetProfileNamesAsync();

        var invalidNameRejected = false;
        try
        {
            await profiles.CreateProfileAsync("invalid__name");
        }
        catch (ArgumentException)
        {
            invalidNameRejected = true;
        }

        var defaultRemovalRejected = false;
        try
        {
            await profiles.RemoveProfileAsync("default");
        }
        catch (InvalidOperationException)
        {
            defaultRemovalRejected = true;
        }

        var unknownProfileRejected = false;
        try
        {
            await profiles.GetProfileAsync("missing");
        }
        catch (KeyNotFoundException)
        {
            unknownProfileRejected = true;
        }

        await profiles.CreateProfileAsync("Work");
        var duplicateProfileRejected = false;
        try
        {
            await profiles.CreateProfileAsync("Work");
        }
        catch (InvalidOperationException)
        {
            duplicateProfileRejected = true;
        }

        (invalidNameRejected).ShouldBeTrue();
        (defaultRemovalRejected).ShouldBeTrue();
        (unknownProfileRejected).ShouldBeTrue();
        (duplicateProfileRejected).ShouldBeTrue();
    }

    [Test]
    public async Task ProfileCatalog_ConcurrentManagersDoNotOverwriteEachOther()
    {
        using var directory = new TemporaryDirectory();
        var filePath = Path.Combine(directory.FullPath, "profiles.json");
        using var firstProvider = CreateServiceProvider(filePath);
        using var secondProvider = CreateServiceProvider(filePath);
        var first = firstProvider.GetRequiredService<IConfiglueProfiledOptions<AppSettings>>();
        var second = secondProvider.GetRequiredService<IConfiglueProfiledOptions<AppSettings>>();
        await Task.WhenAll(
            first.GetProfileNamesAsync().AsTask(),
            second.GetProfileNamesAsync().AsTask()
        );

        var added = await Task.WhenAll(
            TryCreateProfileAsync(first, "First"),
            TryCreateProfileAsync(second, "Second")
        );

        (added.Count(static succeeded => succeeded)).ShouldBe(1);
        if (!added[0])
        {
            await first.CreateProfileAsync("First");
        }

        if (!added[1])
        {
            await second.CreateProfileAsync("Second");
        }

        using var restartedProvider = CreateServiceProvider(filePath);
        var restoredNames = await restartedProvider
            .GetRequiredService<IConfiglueProfiledOptions<AppSettings>>()
            .GetProfileNamesAsync();
        ((restoredNames))
            .OrderBy(static item => item)
            .ShouldBe((new[] { "default", "First", "Second" }).OrderBy(static item => item));
    }

    private static async Task<bool> TryCreateProfileAsync(
        IConfiglueProfiledOptions<AppSettings> profiles,
        string profileName
    )
    {
        try
        {
            await profiles.CreateProfileAsync(profileName);
            return true;
        }
        catch (StateConflictException)
        {
            return false;
        }
    }

    private static ServiceProvider CreateServiceProvider(string filePath)
    {
        var services = new ServiceCollection();
        services.AddSingleton<FileResource>(_ => new FileResource(
            filePath,
            new FileResourceOptions { CreateBackup = false }
        ));
        services.AddConfiglueProfiledOptions<AppSettings, AppSettings.Fragment>(
            (provider, profileName) =>
            {
                var file = provider.GetRequiredService<FileResource>();
                var section = new JsonSectionResource(file, $"Profiles:{profileName}");
                var source = SerializedStateSource.FromResource<AppSettings.Fragment>(
                    profileName,
                    section,
                    new JsonStateCodec()
                );
                return new StateSourceSet<AppSettings.Fragment>([source]);
            },
            provider =>
            {
                var file = provider.GetRequiredService<FileResource>();
                var section = new JsonSectionResource(file, "ProfileCatalog");
                return SerializedStateSource.FromResource<ConfiglueProfileCatalog>(
                    "profile-catalog",
                    section,
                    new JsonStateCodec()
                );
            },
            onChangeDebounce: TimeSpan.Zero
        );
        return services.BuildServiceProvider();
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
