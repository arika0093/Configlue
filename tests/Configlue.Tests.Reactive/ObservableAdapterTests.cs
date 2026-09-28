using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Configlue.Extensions.Reactive;
using Configlue.Tests.ReactiveSupport;

namespace Configlue.Tests.Reactive;

public sealed class ObservableAdapterTests
{
    [Test]
    public async Task NativeSwitchDetachesThePreviousProfileStream()
    {
        var profiles = new FakeProfiles();
        var first = new FakeOptions<int>(1);
        var other = new FakeOptions<int>(10);
        var values = new List<int>();
        using var subscription = profiles
            .ObserveActiveProfileNames()
            .Select(name => (name == "default" ? first : other).ObserveValues())
            .Switch()
            .Subscribe(values.Add);
        await profiles.SetActiveProfileAsync("other");
        first.Emit(2);
        other.Emit(11);
        values.ShouldBe(new[] { 1, 10, 11 });
        first.ValueListeners.ShouldBe(0);
        other.ValueListeners.ShouldBe(1);
    }

    [Test]
    public async Task ConcurrentChangesQueueUntilTheCurrentCallbackCompletes()
    {
        var source = new FakeOptions<int>(0);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var values = new List<int>();
        using var subscription = source
            .ObserveChanges()
            .Subscribe(value =>
            {
                values.Add(value);
                if (value == 1)
                {
                    entered.SetResult();
                    release.Wait();
                }
            });
        var first = Task.Run(() => source.Emit(1));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            source.Emit(2);
            values.ShouldBe(new[] { 1 });
        }
        finally
        {
            release.Set();
        }
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        values.ShouldBe(new[] { 1, 2 });
    }

    [Test]
    public void DisposalSuppressesAnInitialValueWhenTheReadIgnoresCancellation()
    {
        var source = new FakeOptions<int>(1) { PendingRead = new TaskCompletionSource<int>() };
        var values = new List<int>();
        using var subscription = source.ObserveValues().Subscribe(values.Add);
        subscription.Dispose();
        source.PendingRead.SetResult(1);
        values.ShouldBeEmpty();
        source.ReadToken.IsCancellationRequested.ShouldBeTrue();
        source.ValueListeners.ShouldBe(0);
    }

    [Test]
    public void ThrowingInitialObserversDetachAndBackgroundDeliveryDoesNotEscape()
    {
        var source = new FakeOptions<int>(1) { PendingRead = new TaskCompletionSource<int>() };
        using var next = source
            .ObserveValues()
            .Subscribe(_ => throw new InvalidOperationException("consumer next"));
        source.PendingRead.SetResult(1);
        source.ValueListeners.ShouldBe(0);
        source.ReadToken.IsCancellationRequested.ShouldBeTrue();
        var failed = new FakeOptions<int>(1) { PendingRead = new TaskCompletionSource<int>() };
        using var error = failed
            .ObserveValues()
            .Subscribe(_ => { }, _ => throw new InvalidOperationException("consumer error"));
        failed.PendingRead.SetException(new IOException("read error"));
        failed.ValueListeners.ShouldBe(0);
        failed.ReadToken.IsCancellationRequested.ShouldBeTrue();
    }

    [Test]
    public void ActiveValuesAttachToTheConcreteProfileBeforeReadingItsInitialValue()
    {
        var profiles = new FakeProfiles { DeferValueListenerBinding = true };
        var values = new List<int>();
        using var subscription = profiles.ObserveActiveValues().Subscribe(values.Add);
        values.ShouldBe(new[] { 1 });
        profiles.ManagerValueListeners.ShouldBe(0);
        profiles.ProfileOptions["default"].ValueListeners.ShouldBe(1);
        profiles.ProfileOptions["default"].Emit(7);
        values.ShouldBe(new[] { 1, 7 });
    }

    [Test]
    public async Task ActiveValuesSwitchDetachesAndCancelsAnOlderPendingValueRead()
    {
        var profiles = new FakeProfiles { PendingValue = new TaskCompletionSource<int>() };
        var values = new List<int>();
        using var subscription = profiles.ObserveActiveValues().Subscribe(values.Add);
        profiles.ProfileOptions["default"].ValueListeners.ShouldBe(1);
        await profiles.SetActiveProfileAsync("other");
        profiles.ProfileOptions["default"].ValueListeners.ShouldBe(0);
        profiles.ProfileOptions["default"].ReadToken.IsCancellationRequested.ShouldBeTrue();
        profiles.PendingValue.SetResult(1);
        profiles.ProfileOptions["other"].Emit(8);
        values.ShouldBe(new[] { 2, 8 });
        subscription.Dispose();
        profiles.ValueListeners.ShouldBe(0);
        profiles.NameListeners.ShouldBe(0);
    }

    [Test]
    public async Task RapidSwitchesSupersedePendingProfileAcquisition()
    {
        var profiles = new FakeProfiles();
        profiles.PendingProfiles["default"] = new TaskCompletionSource<IWritableOptions<int>>();
        profiles.PendingProfiles["other"] = new TaskCompletionSource<IWritableOptions<int>>();
        profiles.ProfileOptions["last"] = new FakeOptions<int>(3);
        var values = new List<int>();
        using var subscription = profiles.ObserveActiveValues().Subscribe(values.Add);
        await profiles.SetActiveProfileAsync("other");
        await profiles.SetActiveProfileAsync("last");
        profiles.ProfileReadTokens["default"].IsCancellationRequested.ShouldBeTrue();
        profiles.ProfileReadTokens["other"].IsCancellationRequested.ShouldBeTrue();
        profiles.PendingProfiles["default"].SetResult(profiles.ProfileOptions["default"]);
        profiles.PendingProfiles["other"].SetResult(profiles.ProfileOptions["other"]);
        profiles.ProfileOptions["last"].Emit(4);
        values.ShouldBe(new[] { 3, 4 });
        profiles.ProfileOptions["default"].ValueListeners.ShouldBe(0);
        profiles.ProfileOptions["other"].ValueListeners.ShouldBe(0);
    }

    [Test]
    public void NativeComposition_CombinesModelsAndDistinctSelectedState()
    {
        var numbers = new FakeOptions<int>(10);
        var labels = new FakeOptions<string>("first");
        var values = new List<string>();
        using var subscription = numbers
            .ObserveValues()
            .Select(value => value / 10)
            .DistinctUntilChanged()
            .CombineLatest(labels.ObserveValues(), (number, label) => $"{label}:{number}")
            .Subscribe(values.Add);
        numbers.Emit(11);
        numbers.Emit(20);
        labels.Emit("second");
        values.ShouldBe(new[] { "first:1", "first:2", "second:2" });
        numbers.ValueListeners.ShouldBe(1);
        labels.ValueListeners.ShouldBe(1);
        subscription.Dispose();
        numbers.ValueListeners.ShouldBe(0);
        labels.ValueListeners.ShouldBe(0);
    }

    [Test]
    public void ChangesDoNotReadAndValuesSuppressAnOlderInitialCompletion()
    {
        var source = new FakeOptions<int>(1) { PendingRead = new TaskCompletionSource<int>() };
        var changes = new List<int>();
        using var future = source.ObserveChanges().Subscribe(changes.Add);
        source.ReadCount.ShouldBe(0);
        var values = new List<int>();
        using var current = source.ObserveValues().Subscribe(values.Add);
        source.ValueListeners.ShouldBe(2);
        source.ReadCount.ShouldBe(1);
        source.Emit(2);
        source.PendingRead.SetResult(1);
        values.ShouldBe(new[] { 2 });
        changes.ShouldBe(new[] { 2 });
    }

    [Test]
    public void SynchronousAttachCallbacksAndTakeDetachTheReturnedListener()
    {
        var source = new FakeOptions<int>(1) { Attaching = listener => listener(2) };
        var values = new List<int>();
        using var subscription = source.ObserveValues().Take(1).Subscribe(values.Add);
        values.ShouldBe(new[] { 2 });
        source.ValueListeners.ShouldBe(0);
    }

    [Test]
    public void DisposalCancelsInitialReadAndSuppressesIgnoredCancellationCompletion()
    {
        var source = new FakeOptions<int>(1) { PendingRead = new TaskCompletionSource<int>() };
        var values = new List<int>();
        Exception? failure = null;
        var subscription = source.ObserveValues().Subscribe(values.Add, error => failure = error);
        subscription.Dispose();
        source.ReadToken.IsCancellationRequested.ShouldBeTrue();
        source.ValueListeners.ShouldBe(0);
        source.Emit(2);
        source.PendingRead.SetException(new IOException("late ignored cancellation"));
        values.ShouldBeEmpty();
        failure.ShouldBeNull();
    }

    [Test]
    public void InitialReadErrorsTerminateAndReloadFailuresRemainValues()
    {
        var exception = new IOException("initial read");
        var source = new FakeOptions<int>(1) { ReadError = exception };
        Exception? failure = null;
        var values = new List<int>();
        using var initial = source.ObserveValues().Subscribe(values.Add, error => failure = error);
        failure.ShouldBeSameAs(exception);
        source.ValueListeners.ShouldBe(0);
        source.Emit(2);
        values.ShouldBeEmpty();
        var errors = new List<Exception>();
        using var reload = source.ObserveReloadFailures().Subscribe(errors.Add);
        using var recoveredChanges = source.ObserveChanges().Subscribe(values.Add);
        source.Emit(3);
        source.Fail(exception);
        source.Fail(new IOException("later reload"));
        source.Emit(4);
        errors.Count.ShouldBe(2);
        values.ShouldBe(new[] { 3, 4 });
        source.ValueListeners.ShouldBe(1);
        source.FailureListeners.ShouldBe(1);
        reload.Dispose();
        recoveredChanges.Dispose();
        source.FailureListeners.ShouldBe(0);
        source.ValueListeners.ShouldBe(0);
    }

    [Test]
    public void ReentrantChangesAreSerializedOutsideTheObserverCallback()
    {
        var source = new FakeOptions<int>(0);
        var depth = 0;
        var maxDepth = 0;
        var values = new List<int>();
        using var subscription = source
            .ObserveChanges()
            .Subscribe(value =>
            {
                depth++;
                maxDepth = Math.Max(maxDepth, depth);
                values.Add(value);
                if (value == 1)
                {
                    source.Emit(2);
                }
                depth--;
            });
        source.Emit(1);
        values.ShouldBe(new[] { 1, 2 });
        maxDepth.ShouldBe(1);
    }

    [Test]
    public async Task ActiveProfilesObserveSwitchesAndSuppressStaleInitialReads()
    {
        var profiles = new FakeProfiles
        {
            PendingValue = new TaskCompletionSource<int>(),
            PendingName = new TaskCompletionSource<string>(),
        };
        var values = new List<int>();
        var names = new List<string>();
        using var valueSubscription = profiles.ObserveActiveValues().Subscribe(values.Add);
        using var nameSubscription = profiles.ObserveActiveProfileNames().Subscribe(names.Add);
        await profiles.SetActiveProfileAsync("other");
        profiles.PendingValue.SetResult(1);
        profiles.PendingName.SetResult("default");
        values.ShouldBe(new[] { 2 });
        names.ShouldBe(new[] { "other" });
        valueSubscription.Dispose();
        nameSubscription.Dispose();
        profiles.ValueListeners.ShouldBe(0);
        profiles.NameListeners.ShouldBe(0);
    }

    [Test]
    public void NativeThrottleAndDispatchUseDeterministicScheduling()
    {
        var clock = new HistoricalScheduler();
        var context = new ManualContext();
        var source = new FakeOptions<int>(0);
        var values = new List<int>();
        using var subscription = source
            .ObserveChanges()
            .Throttle(TimeSpan.FromSeconds(1), clock)
            .ObserveOn(context)
            .Subscribe(values.Add);
        source.Emit(1);
        clock.AdvanceBy(TimeSpan.FromMilliseconds(500));
        source.Emit(2);
        clock.AdvanceBy(TimeSpan.FromMilliseconds(999));
        context.Run();
        values.ShouldBeEmpty();
        clock.AdvanceBy(TimeSpan.FromMilliseconds(1));
        values.ShouldBeEmpty();
        context.Run();
        values.ShouldBe(new[] { 2 });
    }
}
