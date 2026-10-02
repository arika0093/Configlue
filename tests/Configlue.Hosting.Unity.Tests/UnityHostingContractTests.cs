using Configlue.Hosting.Unity;
using Configlue.Resources;
using UnityEngine;

namespace Configlue.Tests;

/// <summary>
/// Adapter contract tests that build the production Unity hosting implementation against a
/// minimal <c>UnityEngine.CoreModule</c> test double. These validate adapter behavior, not
/// Unity binary/runtime compatibility.
/// </summary>
[NotInParallel]
public sealed class UnityHostingContractTests
{
    [Test]
    public void UseUnityMapsPersistentDataPathAndBackupRoot()
    {
        using var unity = UnityTestScope.Enter();
        var root = Path.Combine(Path.GetTempPath(), "unity-" + Guid.NewGuid().ToString("N"));
        Application.persistentDataPath = root;
        var paths = new UnityHostPaths();

        paths.TryResolve(ConfiglueStandardLocation.UserGlobal, "app", out var user).ShouldBeTrue();
        user.ShouldBe(root);

        paths
            .TryResolve(ConfiglueStandardLocation.BackupRoot, "app", out var backup)
            .ShouldBeTrue();
        backup.ShouldBe(Path.Combine(root, "Configlue", "Backups"));
    }

    [Test]
    public void UnsupportedStandardLocationsAreRejected()
    {
        using var unity = UnityTestScope.Enter();
        Application.persistentDataPath = Path.GetTempPath();
        var paths = new UnityHostPaths();

        paths.TryResolve(ConfiglueStandardLocation.HostGlobal, "app", out var host).ShouldBeFalse();
        host.ShouldBeEmpty();

        paths.TryResolve(ConfiglueStandardLocation.Local, "app", out var local).ShouldBeFalse();
        local.ShouldBeEmpty();
    }

    [Test]
    public void EmptyPersistentDataPathIsRejected()
    {
        using var unity = UnityTestScope.Enter();
        Application.persistentDataPath = "   ";
        var paths = new UnityHostPaths();

        paths.TryResolve(ConfiglueStandardLocation.UserGlobal, "app", out var user).ShouldBeFalse();
        user.ShouldBeEmpty();
    }

    [Test]
    public void UseUnityRegistersHostPathsOnTheBuilder()
    {
        using var unity = UnityTestScope.Enter();
        Application.persistentDataPath = Path.GetTempPath();
        var builder = new ConfiglueBuilder();

        builder.UseUnity().ShouldBeSameAs(builder);
    }

    [Test]
    public void DispatcherReportsAccessOnTheMainThread()
    {
        using var unity = UnityTestScope.Enter();
        SynchronizationContext.SetSynchronizationContext(new UnitySynchronizationContext());
        var dispatcher = new UnityConfiglueDispatcher();

        dispatcher.CheckAccess().ShouldBeTrue();
    }

    [Test]
    public void DispatcherPostsThroughTheCapturedContext()
    {
        using var unity = UnityTestScope.Enter();
        var context = new UnitySynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(context);
        var dispatcher = new UnityConfiglueDispatcher();
        var executed = false;

        dispatcher.Post(() => executed = true);

        executed.ShouldBeFalse();
        context.RunPending();
        executed.ShouldBeTrue();
    }

    [Test]
    public void DispatcherRejectsPostsAfterExit()
    {
        using var unity = UnityTestScope.Enter();
        SynchronizationContext.SetSynchronizationContext(new UnitySynchronizationContext());
        using var exit = new CancellationTokenSource();
        Application.exitCancellationToken = exit.Token;
        var dispatcher = new UnityConfiglueDispatcher();
        exit.Cancel();

        dispatcher.CheckAccess().ShouldBeFalse();
        Should.Throw<OperationCanceledException>(() => dispatcher.Post(() => { }));
    }

    [Test]
    public void DispatcherSkipsQueuedWorkAfterExit()
    {
        using var unity = UnityTestScope.Enter();
        var context = new UnitySynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(context);
        using var exit = new CancellationTokenSource();
        Application.exitCancellationToken = exit.Token;
        var dispatcher = new UnityConfiglueDispatcher();
        var executed = false;

        dispatcher.Post(() => executed = true);
        exit.Cancel();
        context.RunPending();

        executed.ShouldBeFalse();
    }

    [Test]
    public void DispatcherRejectsMissingUnitySynchronizationContext()
    {
        using var unity = UnityTestScope.Enter();
        SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());

        Should.Throw<InvalidOperationException>(() =>
        {
            _ = new UnityConfiglueDispatcher();
        });
    }

    [Test]
    public void DispatcherAndHostPathsRejectNonMainThreadInitialization()
    {
        using var unity = UnityTestScope.Enter();
        Awaitable.IsMainThread = false;

        Should.Throw<InvalidOperationException>(() =>
        {
            _ = new UnityHostPaths();
        });
        Should.Throw<InvalidOperationException>(() =>
        {
            _ = new UnityConfiglueDispatcher();
        });
    }

    private readonly struct UnityTestScope : IDisposable
    {
        private readonly string _persistentDataPath;
        private readonly CancellationToken _exitCancellation;
        private readonly bool _isMainThread;
        private readonly SynchronizationContext? _synchronizationContext;

        private UnityTestScope(
            string persistentDataPath,
            CancellationToken exitCancellation,
            bool isMainThread,
            SynchronizationContext? synchronizationContext
        )
        {
            _persistentDataPath = persistentDataPath;
            _exitCancellation = exitCancellation;
            _isMainThread = isMainThread;
            _synchronizationContext = synchronizationContext;
        }

        public static UnityTestScope Enter()
        {
            var scope = new UnityTestScope(
                Application.persistentDataPath,
                Application.exitCancellationToken,
                Awaitable.IsMainThread,
                SynchronizationContext.Current
            );
            Application.persistentDataPath = Path.GetTempPath();
            Application.exitCancellationToken = CancellationToken.None;
            Awaitable.IsMainThread = true;
            SynchronizationContext.SetSynchronizationContext(null);
            return scope;
        }

        public void Dispose()
        {
            Application.persistentDataPath = _persistentDataPath;
            Application.exitCancellationToken = _exitCancellation;
            Awaitable.IsMainThread = _isMainThread;
            SynchronizationContext.SetSynchronizationContext(_synchronizationContext);
        }
    }
}
