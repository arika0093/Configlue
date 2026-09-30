using Configlue.Source.Presets;

namespace Configlue.Tests;

public sealed class ConfiglueStandardPathsTests
{
    [Test]
    public void StandardSaveDirectoryUsesThePlatformConfigurationBase()
    {
        var applicationId = "configlue-standard-path-test";
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetEnvironmentVariable("APPDATA");
        var xdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var expectedBase = OperatingSystem.IsWindows()
            ? string.IsNullOrWhiteSpace(appData)
                ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
                : appData
            : string.IsNullOrWhiteSpace(xdgConfigHome)
                ? OperatingSystem.IsMacOS()
                    ? Path.Combine(profile, "Library", "Application Support")
                    : Path.Combine(profile, ".config")
                : xdgConfigHome;

        var path = ConfiglueStandardPaths.GetStandardSaveDirectory(applicationId);

        (Path.IsPathFullyQualified(path)).ShouldBeTrue();
        (path).ShouldBe(Path.GetFullPath(Path.Combine(expectedBase, applicationId)));
    }

    [Test]
    public void StandardSaveDirectoryRejectsPathComponentsAsApplicationIdentifiers()
    {
        Should.Throw<ArgumentException>(() =>
            ConfiglueStandardPaths.GetStandardSaveDirectory("../outside")
        );
    }

    [Test]
    public void PersistentUserDataDirectoryUsesThePlatformStateBase()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        var xdgStateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        var expectedPath =
            OperatingSystem.IsWindows()
                ? Path.GetFullPath(
                    string.IsNullOrWhiteSpace(localAppData)
                        ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                        : localAppData
                )
            : OperatingSystem.IsMacOS()
                ? Path.GetFullPath(Path.Combine(profile, "Library", "Application Support"))
            : Path.GetFullPath(
                string.IsNullOrWhiteSpace(xdgStateHome)
                    ? Path.Combine(profile, ".local", "state")
                    : xdgStateHome
            );

        ConfiglueStandardPaths.GetPersistentUserDataDirectory().ShouldBe(expectedPath);
    }

    [Test]
    public void HostProfileResolvesDistinctLocationsAndRejectsUnsupportedLocations()
    {
        using var directory = new TemporaryDirectory();
        var hostGlobal = Path.Combine(directory.FullPath, "machine");
        var userGlobal = Path.Combine(directory.FullPath, "user");
        var local = Path.Combine(directory.FullPath, "deployment");
        var backupRoot = Path.Combine(directory.FullPath, "backups");
        var profile = ConfiglueHostPathProfile
            .Default.WithOverride(ConfiglueStandardLocation.HostGlobal, _ => hostGlobal)
            .WithOverride(ConfiglueStandardLocation.UserGlobal, _ => userGlobal)
            .WithOverride(ConfiglueStandardLocation.Local, _ => local)
            .WithOverride(ConfiglueStandardLocation.BackupRoot, _ => backupRoot);

        ConfiglueStandardPaths
            .ResolveDirectory(profile, ConfiglueStandardLocation.HostGlobal, "Example")
            .ShouldBe(Path.GetFullPath(hostGlobal));
        ConfiglueStandardPaths
            .ResolveDirectory(profile, ConfiglueStandardLocation.UserGlobal, "Example")
            .ShouldBe(Path.GetFullPath(userGlobal));
        ConfiglueStandardPaths
            .ResolveDirectory(profile, ConfiglueStandardLocation.Local)
            .ShouldBe(Path.GetFullPath(local));
        ConfiglueStandardPaths
            .ResolveDirectory(profile, ConfiglueStandardLocation.BackupRoot)
            .ShouldBe(Path.GetFullPath(backupRoot));

        var sandboxed = profile.WithOverride(ConfiglueStandardLocation.HostGlobal, _ => null);
        Should.Throw<NotSupportedException>(() =>
            ConfiglueStandardPaths.ResolveDirectory(
                sandboxed,
                ConfiglueStandardLocation.HostGlobal,
                "Example"
            )
        );
    }

    [Test]
    public async Task CommonSourcesUseHostPathsAndPreferUserGlobalOverHostGlobal()
    {
        using var directory = new TemporaryDirectory();
        var hostGlobal = Path.Combine(directory.FullPath, "machine");
        var userGlobal = Path.Combine(directory.FullPath, "user");
        var profile = ConfiglueHostPathProfile
            .Default.WithOverride(ConfiglueStandardLocation.HostGlobal, _ => hostGlobal)
            .WithOverride(ConfiglueStandardLocation.UserGlobal, _ => userGlobal);

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.UseHostPaths(profile);
            builder.UseCommonSources(sources =>
            {
                sources.WithHostGlobal("Example");
                sources.WithUserGlobal("Example");
                sources.Add<AppSettings>();
            });
        });

        var diagnostics = (
            (IConfiglueRuntimeState<AppSettings>)context.GetState<AppSettings>()
        ).GetDiagnostics();
        var userSource = diagnostics.Sources.Single(source => source.Priority == 100);
        diagnostics.DefaultWriteSourceId.ShouldBe(userSource.Id);
        diagnostics
            .Sources.Single(source => source.Priority == 0)
            .PhysicalOrigin.ShouldBe(Path.Combine(hostGlobal, "settings.json"));
        userSource.PhysicalOrigin.ShouldBe(Path.Combine(userGlobal, "settings.json"));
        userSource.CanWrite.ShouldBeTrue();
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
