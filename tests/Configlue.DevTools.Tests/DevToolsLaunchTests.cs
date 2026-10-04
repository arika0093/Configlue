using Configlue.DevTools.Web;

namespace Configlue.DevTools.Tests;

[NotInParallel]
public sealed class DevToolsLaunchTests
{
    private const string LoopbackUrl = "http://127.0.0.1:5123/?token=abc123";
    private const string LoopbackUrl2 = "http://127.0.0.1:5124/?token=def456";

    [Test]
    public async Task DisabledSessionDoesNotLaunch()
    {
        Reset();
        var launcher = new FakeLauncher();
        ConfiglueDevTools.IsEnabled.ShouldBeFalse();

        (await ConfiglueDevTools.OpenBrowserAsync(launcher)).ShouldBeFalse();
        launcher.Urls.Count.ShouldBe(0);

        (await ConfiglueDevTools.OpenBrowserAsync((string?)null, launcher)).ShouldBeFalse();
        (await ConfiglueDevTools.OpenBrowserAsync(string.Empty, launcher)).ShouldBeFalse();
        launcher.Urls.Count.ShouldBe(0);
        Reset();
    }

    [Test]
    public async Task NotStartedSessionDoesNotLaunch()
    {
        Reset();
        ConfiglueDevTools.Enable(LoopbackUrl);
        ConfiglueDevTools.Clear();
        var launcher = new FakeLauncher();

        (await ConfiglueDevTools.OpenBrowserAsync(launcher)).ShouldBeFalse();
        launcher.Urls.Count.ShouldBe(0);
        Reset();
    }

    [Test]
    public async Task AlreadyRunningReusesSameUrlAcrossMultiOpen()
    {
        Reset();
        ConfiglueDevTools.Enable(LoopbackUrl);
        var launcher = new FakeLauncher();

        (await ConfiglueDevTools.OpenBrowserAsync(launcher)).ShouldBeTrue();
        (await ConfiglueDevTools.OpenBrowserAsync(launcher)).ShouldBeTrue();
        launcher.Urls.Count.ShouldBe(2);
        launcher.Urls[0].ShouldBe(LoopbackUrl);
        launcher.Urls[1].ShouldBe(LoopbackUrl);
        ConfiglueDevTools.CurrentLaunchUrl.ShouldBe(LoopbackUrl);
        Reset();
    }

    [Test]
    public async Task ShutdownDisablesFurtherLaunches()
    {
        Reset();
        ConfiglueDevTools.Enable(LoopbackUrl);
        var launcher = new FakeLauncher();
        (await ConfiglueDevTools.OpenBrowserAsync(launcher)).ShouldBeTrue();

        ConfiglueDevTools.Disable();
        (await ConfiglueDevTools.OpenBrowserAsync(launcher)).ShouldBeFalse();
        launcher.Urls.Count.ShouldBe(1);

        ConfiglueDevTools.Enable(LoopbackUrl2);
        (await ConfiglueDevTools.OpenBrowserAsync(launcher)).ShouldBeTrue();
        launcher.Urls[1].ShouldBe(LoopbackUrl2);

        ConfiglueDevTools.Clear();
        (await ConfiglueDevTools.OpenBrowserAsync(launcher)).ShouldBeFalse();
        launcher.Urls.Count.ShouldBe(2);
        Reset();
    }

    [Test]
    public async Task PortAndTokenArePreservedVerbatim()
    {
        Reset();
        const string url = "http://127.0.0.1:59384/?token=0123456789abcdef0123456789abcdef";
        ConfiglueDevTools.Enable(url);
        var launcher = new FakeLauncher();
        (await ConfiglueDevTools.OpenBrowserAsync(launcher)).ShouldBeTrue();
        launcher.Urls.Count.ShouldBe(1);
        launcher.Urls[0].ShouldBe(url);
        launcher.Urls[0].ShouldContain("59384");
        launcher.Urls[0].ShouldContain("token=");
        Reset();
    }

    [Test]
    public async Task NonLoopbackUrlsAreRejected()
    {
        Reset();
        Should.Throw<ArgumentException>(() =>
            ConfiglueDevTools.Enable("http://example.com/?token=x")
        );
        Should.Throw<ArgumentException>(() =>
            ConfiglueDevTools.Enable("https://127.0.0.1:1/?token=x")
        );
        var launcher = new FakeLauncher();
        await Should.ThrowAsync<ArgumentException>(async () =>
            await ConfiglueDevTools.OpenBrowserAsync("http://example.com/?token=x", launcher)
        );
        launcher.Urls.Count.ShouldBe(0);
        Reset();
    }

    [Test]
    public async Task FuncLauncherOverloadIsInvoked()
    {
        Reset();
        ConfiglueDevTools.Enable(LoopbackUrl);
        string? seen = null;
        var opened = await ConfiglueDevTools.OpenBrowserAsync(
            (url, _) =>
            {
                seen = url;
                return Task.CompletedTask;
            }
        );
        opened.ShouldBeTrue();
        seen.ShouldBe(LoopbackUrl);
        Reset();
    }

    [Test]
    public async Task WebHostNotStartedDoesNotLaunchAndPublishThrows()
    {
        Reset();
        await using var context = DevToolsFixtures.CreateDemoContext();
        var registry = new ConfiglueDevToolsRegistry();
        registry.Add(context.GetState<DevToolsDemoSettings>());
        var host = ConfiglueDevToolsWebHost.Create(registry);
        try
        {
            var launcher = new FakeLauncher();
            (await host.OpenBrowserAsync(launcher)).ShouldBeFalse();
            launcher.Urls.Count.ShouldBe(0);
            host.IsRunning.ShouldBeFalse();
            Should.Throw<InvalidOperationException>(() => host.PublishAsCurrentSession());
        }
        finally
        {
            host.Dispose();
            Reset();
        }
    }

    [Test]
    public async Task WebHostOpenReusesRunningUrlWithoutDuplicateStartup()
    {
        Reset();
        await using var context = DevToolsFixtures.CreateDemoContext();
        var registry = new ConfiglueDevToolsRegistry();
        registry.Add(context.GetState<DevToolsDemoSettings>());
        await using var host = ConfiglueDevToolsWebHost.Create(registry);
        await host.StartAsync();
        try
        {
            var launcher = new FakeLauncher();
            (await host.OpenBrowserAsync(launcher)).ShouldBeTrue();
            (await host.OpenBrowserAsync(launcher)).ShouldBeTrue();
            launcher.Urls.Count.ShouldBe(2);
            launcher.Urls[0].ShouldBe(host.LaunchUrl);
            launcher.Urls[1].ShouldBe(host.LaunchUrl);
            host.IsRunning.ShouldBeTrue();

            host.PublishAsCurrentSession();
            ConfiglueDevTools.IsEnabled.ShouldBeTrue();
            ConfiglueDevTools.CurrentLaunchUrl.ShouldBe(host.LaunchUrl);

            var sessionLauncher = new FakeLauncher();
            (await ConfiglueDevTools.OpenBrowserAsync(sessionLauncher)).ShouldBeTrue();
            sessionLauncher.Urls[0].ShouldBe(host.LaunchUrl);
        }
        finally
        {
            await host.StopAsync();
            Reset();
        }
    }

    [Test]
    public async Task WebHostShutdownStopsLaunching()
    {
        Reset();
        await using var context = DevToolsFixtures.CreateDemoContext();
        var registry = new ConfiglueDevToolsRegistry();
        registry.Add(context.GetState<DevToolsDemoSettings>());
        var host = ConfiglueDevToolsWebHost.Create(registry);
        await host.StartAsync();
        host.PublishAsCurrentSession();
        await host.StopAsync();
        try
        {
            var launcher = new FakeLauncher();
            (await host.OpenBrowserAsync(launcher)).ShouldBeFalse();
            launcher.Urls.Count.ShouldBe(0);
        }
        finally
        {
            host.Dispose();
            Reset();
        }
    }

    [Test]
    public async Task AspNetCoreHelperInvokesLauncherAndRespectsDisabled()
    {
        Reset();
        var launcher = new FakeLauncher();
        (
            await global::Configlue.Hosting.AspNetCore.AspNetCoreConfiglueDevTools.OpenBrowserAsync(
                launcher
            )
        ).ShouldBeFalse();
        launcher.Urls.Count.ShouldBe(0);

        ConfiglueDevTools.Enable(LoopbackUrl);
        (
            await global::Configlue.Hosting.AspNetCore.AspNetCoreConfiglueDevTools.OpenBrowserAsync(
                launcher
            )
        ).ShouldBeTrue();
        launcher.Urls[0].ShouldBe(LoopbackUrl);

        var explicitLauncher = new FakeLauncher();
        (
            await global::Configlue.Hosting.AspNetCore.AspNetCoreConfiglueDevTools.OpenBrowserAsync(
                LoopbackUrl2,
                explicitLauncher
            )
        ).ShouldBeTrue();
        explicitLauncher.Urls[0].ShouldBe(LoopbackUrl2);
        Reset();
    }

    [Test]
    public async Task BlazorHelperReusesSharedSession()
    {
        Reset();
        ConfiglueDevTools.Enable(LoopbackUrl);
        var launcher = new FakeLauncher();
        (
            await global::Configlue.Hosting.Blazor.BlazorConfiglueDevTools.OpenBrowserAsync(
                launcher
            )
        ).ShouldBeTrue();
        launcher.Urls[0].ShouldBe(LoopbackUrl);
        Reset();
    }

    [Test]
    public async Task AvaloniaHelperInvokesLauncher()
    {
        Reset();
        ConfiglueDevTools.Enable(LoopbackUrl);
        var launcher = new FakeLauncher();
        (
            await global::Configlue.Hosting.Avalonia.AvaloniaConfiglueDevTools.OpenBrowserAsync(
                launcher
            )
        ).ShouldBeTrue();
        launcher.Urls[0].ShouldBe(LoopbackUrl);
        Reset();
    }

    [Test]
    public async Task GodotHelperInvokesLauncher()
    {
        Reset();
        ConfiglueDevTools.Enable(LoopbackUrl);
        var launcher = new FakeLauncher();
        (
            await global::Configlue.Hosting.Godot.GodotConfiglueDevTools.OpenBrowserAsync(launcher)
        ).ShouldBeTrue();
        launcher.Urls[0].ShouldBe(LoopbackUrl);
        Reset();
    }

    [Test]
    public void UnityMenuRemainsEditorOnly()
    {
        var menu = ReadHostingSource(
            "Configlue.Hosting.Unity",
            "Editor/UnityConfiglueDevToolsMenu.cs"
        );
        menu.ShouldContain("#if UNITY_EDITOR");
        menu.ShouldContain("[MenuItem(\"Tools/Configlue/Open DevTools\")]");
        menu.ShouldContain("Application.OpenURL");
        menu.ShouldNotContain("UIToolkit");
        menu.ShouldNotContain("WebView");
    }

    [Test]
    public void UnityRuntimeHelperUsesPlatformLauncher()
    {
        var runtime = ReadHostingSource("Configlue.Hosting.Unity", "UnityConfiglueDevTools.cs");
        runtime.ShouldContain("Application.OpenURL");
        runtime.ShouldContain("IConfiglueDevToolsBrowserLauncher");
        runtime.ShouldNotContain("WebView");
        runtime.ShouldNotContain("UIToolkit");
    }

    [Test]
    public void GodotEditorPluginRemainsEditorOnly()
    {
        var plugin = ReadHostingSource(
            "Configlue.Hosting.Godot",
            "ConfiglueDevToolsEditorPlugin.cs"
        );
        plugin.ShouldContain("#if TOOLS");
        plugin.ShouldContain("AddToolMenuItem");
        plugin.ShouldContain("OS.ShellOpen");
        plugin.ShouldNotContain("WebView");
    }

    [Test]
    public void HostingPackagesHaveNoWebViewOrBlazorMonacoDependencies()
    {
        foreach (
            var package in new[]
            {
                "Configlue.Hosting.AspNetCore",
                "Configlue.Hosting.Blazor",
                "Configlue.Hosting.Avalonia",
                "Configlue.Hosting.Maui",
                "Configlue.Hosting.Godot",
                "Configlue.Hosting.Unity",
                "Configlue.Hosting.Wpf",
                "Configlue.Hosting.WinForms",
                "Configlue.Hosting.WinUI",
            }
        )
        {
            var directory = FindHostingDirectory(package);
            var project = File.ReadAllText(
                Directory.GetFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly)[0]
            );
            project.ShouldNotContain("WebView2");
            project.ShouldNotContain("BlazorWebView");
            project.ShouldNotContain("BlazorMonaco");
            project.ShouldNotContain("Configlue.DevTools.Web");
            foreach (var file in Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                var text = File.ReadAllText(file);
                text.ShouldNotContain("Microsoft.AspNetCore.Components.WebView");
                text.ShouldNotContain("BlazorMonaco");
            }
        }
    }

    private static void Reset()
    {
        ConfiglueDevTools.Disable();
        ConfiglueDevTools.Clear();
    }

    private sealed class FakeLauncher : IConfiglueDevToolsBrowserLauncher
    {
        public List<string> Urls { get; } = [];

        public Task OpenAsync(string url, CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            Urls.Add(url);
            return Task.CompletedTask;
        }
    }

    private static string ReadHostingSource(string package, string relativePath)
    {
        var directory = FindHostingDirectory(package);
        var file = Path.Combine(directory, relativePath);
        return File.ReadAllText(file);
    }

    private static string FindHostingDirectory(string package)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "src", "hosting", package);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            candidate = Path.Combine(current.FullName, "hosting", package);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Hosting package directory '{package}' was not found."
        );
    }
}
