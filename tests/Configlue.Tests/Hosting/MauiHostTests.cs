using System.Collections.Concurrent;
using Configlue.Hosting.Tests;
using Configlue.Provider.Json;
using Microsoft.Maui.Storage;

namespace Configlue.Hosting.Maui.Tests;

public sealed class MauiHostTests
{
    [Test]
    public async Task SecureStorage_ComposesWithGeneratedFragment_AndJsonSourcePipeline()
    {
        var resource = new SecureStorageResource(new FakeStorage(), "secret");
        var source = SerializedStateSource.FromResource<HostSettings.Fragment>(
            "secure",
            resource,
            new JsonStateCodec<HostSettings.Fragment>()
        );
        var written = await source.WriteAsync(
            ConfiglueResourceContext.Default,
            new StateWriteRequest<HostSettings.Fragment>(
                new() { Counter = 7 },
                RevisionCondition.MustNotExist
            )
        );
        var read = await source.ReadAsync(ConfiglueResourceContext.Default);
        read.Status.ShouldBe(StateReadStatus.Success);
        read.Value!.ToModel().Counter.ShouldBe(7);
        read.Revision.ShouldBe(written.Revision);
        await source.WriteAsync(
            ConfiglueResourceContext.Default,
            new StateWriteRequest<HostSettings.Fragment>(
                new() { Counter = 8 },
                RevisionCondition.Match(read.Revision!)
            )
        );
        (await source.ReadAsync(ConfiglueResourceContext.Default))
            .Value!.ToModel()
            .Counter.ShouldBe(8);
    }

    [Test]
    public void UseMaui_UsesAppIsolatedPersistentPaths_AndRejectsUnsupportedLocations()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "maui-app-data"));
        var builder = new ConfiglueBuilder().UseMaui(new FakeFileSystem(root));
        builder
            .ResolveStandardDirectory(ConfiglueStandardLocation.UserGlobal, "ignored-app-id")
            .ShouldBe(root);
        builder
            .ResolveStandardDirectory(ConfiglueStandardLocation.BackupRoot)
            .ShouldBe(Path.Combine(root, "Configlue", "Backups"));
        Should.Throw<NotSupportedException>(() =>
            builder.ResolveStandardDirectory(ConfiglueStandardLocation.HostGlobal, "app")
        );
        Should.Throw<NotSupportedException>(() =>
            builder.ResolveStandardDirectory(ConfiglueStandardLocation.Local)
        );
    }

    [Test]
    public async Task SecureStorage_BinaryAndEmptyPayloadsRoundTrip_AndMissingIsDistinct()
    {
        var storage = new FakeStorage();
        var resource = new SecureStorageResource(storage, "secret");
        (await resource.ReadAsync(default)).Status.ShouldBe(StateReadStatus.NotFound);
        var bytes = Enumerable.Range(0, 256).Select(static value => (byte)value).ToArray();
        var written = await resource.WriteAsync(
            default,
            new ResourceWriteRequest(bytes, RevisionCondition.MustNotExist)
        );
        var read = await resource.ReadAsync(default);
        read.Content.ToArray().ShouldBe(bytes);
        read.Revision.ShouldBe(written.Revision);
        storage.Values["secret"].ShouldBe(Convert.ToBase64String(bytes));
        await resource.WriteAsync(
            default,
            new ResourceWriteRequest(
                ReadOnlyMemory<byte>.Empty,
                RevisionCondition.Match(read.Revision!)
            )
        );
        var empty = await resource.ReadAsync(default);
        empty.Status.ShouldBe(StateReadStatus.Success);
        empty.Content.IsEmpty.ShouldBeTrue();
    }

    [Test]
    [Arguments("not-base64!")]
    [Arguments("AAECAwQ=")]
    public async Task SecureStorage_InvalidOrOversizedStoredPayloads_ReportInvalidPayload(
        string raw
    )
    {
        var storage = new FakeStorage();
        storage.Values["secret"] = raw;
        var resource = new SecureStorageResource(storage, "secret", maximumContentBytes: 2);
        (await resource.ReadAsync(default)).Status.ShouldBe(StateReadStatus.InvalidPayload);
    }

    [Test]
    public async Task SecureStorage_RejectsOversizedWritesBeforeCallingNativeStorage()
    {
        var storage = new FakeStorage();
        var resource = new SecureStorageResource(storage, "secret", maximumContentBytes: 2);
        await Should.ThrowAsync<ArgumentException>(async () =>
            await resource.WriteAsync(default, new ResourceWriteRequest(new byte[3]))
        );
        storage.Values.ShouldBeEmpty();
    }

    [Test]
    public async Task SecureStorage_ConflictingConditionsPreserveExistingSecret()
    {
        var storage = new FakeStorage();
        var resource = new SecureStorageResource(storage, "secret");
        await resource.WriteAsync(default, new ResourceWriteRequest(new byte[] { 1 }));
        await Should.ThrowAsync<StateConflictException>(async () =>
            await resource.WriteAsync(
                default,
                new ResourceWriteRequest(new byte[] { 2 }, RevisionCondition.MustNotExist)
            )
        );
        await Should.ThrowAsync<StateConflictException>(async () =>
            await resource.WriteAsync(
                default,
                new ResourceWriteRequest(new byte[] { 2 }, RevisionCondition.Match("stale"))
            )
        );
        (await resource.ReadAsync(default)).Content.ToArray().ShouldBe(new byte[] { 1 });
    }

    [Test]
    public async Task SecureStorage_SharedServiceAndKey_SerializeConditionalWritesAcrossResourceInstances()
    {
        var storage = new FakeStorage();
        var first = new SecureStorageResource(storage, "secret");
        var second = new SecureStorageResource(storage, "secret");
        var original = await first.WriteAsync(default, new ResourceWriteRequest(new byte[] { 0 }));
        var entered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        storage.BeforeGet = async () =>
        {
            entered.TrySetResult(true);
            await release.Task;
        };
        var firstWrite = first
            .WriteAsync(
                default,
                new ResourceWriteRequest(
                    new byte[] { 1 },
                    RevisionCondition.Match(original.Revision!)
                )
            )
            .AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var secondWrite = second
            .WriteAsync(
                default,
                new ResourceWriteRequest(
                    new byte[] { 2 },
                    RevisionCondition.Match(original.Revision!)
                )
            )
            .AsTask();
        secondWrite.IsCompleted.ShouldBeFalse();
        release.SetResult(true);
        await firstWrite;
        await Should.ThrowAsync<StateConflictException>(async () => await secondWrite);
        (await first.ReadAsync(default)).Content.ToArray().ShouldBe(new byte[] { 1 });
    }

    [Test]
    public async Task SecureStorage_SnapshotsCallerMemoryBeforeAwaitingNativeStorage()
    {
        var storage = new FakeStorage();
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        storage.BeforeGet = () => release.Task;
        var resource = new SecureStorageResource(storage, "secret");
        var bytes = new byte[] { 1 };
        var writing = resource
            .WriteAsync(default, new ResourceWriteRequest(bytes, RevisionCondition.MustNotExist))
            .AsTask();
        bytes[0] = 2;
        release.SetResult(true);
        await writing;
        (await resource.ReadAsync(default)).Content.ToArray().ShouldBe(new byte[] { 1 });
    }

    [Test]
    public async Task SecureStorage_CancellationBeforeCommit_PreventsWrite_AndAfterCommitReturnsRevision()
    {
        var storage = new FakeStorage();
        var resource = new SecureStorageResource(storage, "secret");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await resource.WriteAsync(
                default,
                new ResourceWriteRequest(new byte[] { 1 }),
                canceled.Token
            )
        );
        storage.Values.ShouldBeEmpty();
        using var duringCommit = new CancellationTokenSource();
        storage.AfterSet = duringCommit.Cancel;
        var result = await resource.WriteAsync(
            default,
            new ResourceWriteRequest(new byte[] { 1 }),
            duringCommit.Token
        );
        result.Revision.ShouldNotBeNull();
        (await resource.ReadAsync(default)).Revision.ShouldBe(result.Revision);
    }

    [Test]
    public async Task SecureStorage_PreservesNativeFailuresForCallerRecovery()
    {
        var failure = new InvalidOperationException("native storage unavailable");
        var storage = new FakeStorage { BeforeGet = () => throw failure };
        var resource = new SecureStorageResource(storage, "secret");
        var observed = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await resource.ReadAsync(default)
        );
        observed.ShouldBeSameAs(failure);
    }

    private sealed class FakeFileSystem(string root) : IFileSystem
    {
        public string AppDataDirectory => root;
        public string CacheDirectory => Path.Combine(root, "Cache");

        public Task<Stream> OpenAppPackageFileAsync(string filename) =>
            throw new NotSupportedException();

        public Task<bool> AppPackageFileExistsAsync(string filename) => Task.FromResult(false);
    }

    private sealed class FakeStorage : ISecureStorage
    {
        internal ConcurrentDictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
        internal Func<Task>? BeforeGet { get; set; }
        internal Action? AfterSet { get; set; }

        public async Task<string?> GetAsync(string key)
        {
            if (BeforeGet is { } beforeGet)
                await beforeGet();
            return Values.TryGetValue(key, out var value) ? value : null;
        }

        public Task SetAsync(string key, string value)
        {
            Values[key] = value;
            AfterSet?.Invoke();
            return Task.CompletedTask;
        }

        public bool Remove(string key) => Values.TryRemove(key, out _);

        public void RemoveAll() => Values.Clear();
    }
}
