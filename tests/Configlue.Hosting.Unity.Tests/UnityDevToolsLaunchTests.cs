using Configlue.DevTools;
using Configlue.DevTools.Unity;
using UnityEngine;

namespace Configlue.Tests;

[NotInParallel]
public sealed class UnityDevToolsLaunchTests
{
    private const string LoopbackUrl = "http://127.0.0.1:5123/?token=abc123";

    [Test]
    public async Task UnityHelperInvokesLauncherAndRespectsDisabled()
    {
        Reset();
        var launcher = new FakeLauncher();
        (await UnityConfiglueDevTools.OpenBrowserAsync(launcher)).ShouldBeFalse();
        launcher.Urls.Count.ShouldBe(0);

        ConfiglueDevTools.Enable(LoopbackUrl);
        (await UnityConfiglueDevTools.OpenBrowserAsync(launcher)).ShouldBeTrue();
        launcher.Urls[0].ShouldBe(LoopbackUrl);
        Reset();
    }

    [Test]
    public async Task UnityDefaultLauncherUsesApplicationOpenUrl()
    {
        Reset();
        Application.OpenedUrls.Clear();
        ConfiglueDevTools.Enable(LoopbackUrl);
        try
        {
            (await UnityConfiglueDevTools.OpenBrowserAsync()).ShouldBeTrue();
            Application.OpenedUrls.Count.ShouldBe(1);
            Application.OpenedUrls[0].ShouldBe(LoopbackUrl);
        }
        finally
        {
            Application.OpenedUrls.Clear();
            Reset();
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
}
