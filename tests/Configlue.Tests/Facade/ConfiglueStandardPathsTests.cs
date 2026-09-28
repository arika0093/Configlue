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
}
