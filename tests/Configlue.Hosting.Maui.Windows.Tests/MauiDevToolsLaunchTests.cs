using Configlue.DevTools;

namespace Configlue.Hosting.Maui.Windows.Tests;

[NotInParallel]
public sealed class MauiDevToolsLaunchTests
{
    private const string LoopbackUrl = "http://127.0.0.1:5123/?token=abc123";

    [Test]
    public async Task MauiHelperInvokesLauncherAndRespectsDisabled()
    {
        Reset();
        var launcher = new FakeLauncher();
        (await MauiConfiglueDevTools.OpenBrowserAsync(launcher)).ShouldBeFalse();
        launcher.Urls.Count.ShouldBe(0);

        ConfiglueDevTools.Enable(LoopbackUrl);
        (await MauiConfiglueDevTools.OpenBrowserAsync(launcher)).ShouldBeTrue();
        launcher.Urls[0].ShouldBe(LoopbackUrl);
        Reset();
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
