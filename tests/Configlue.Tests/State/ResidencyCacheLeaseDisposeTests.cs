using Configlue.Internal;

namespace Configlue.Tests;

public sealed class ResidencyCacheLeaseDisposeTests
{
    [Test]
    public void DisposeDuringActiveLeaseDefersValueDisposalUntilRelease()
    {
        var value = new RecordingDisposable();
        var cache = new ResidencyCache<int, RecordingDisposable>(_ => value);

        var lease = cache.Acquire(1);
        cache.Dispose();

        value.DisposeCount.ShouldBe(0);
        lease.Value.ShouldBeSameAs(value);

        lease.Dispose();
        value.DisposeCount.ShouldBe(1);

        cache.Dispose();
        value.DisposeCount.ShouldBe(1);
    }

    [Test]
    public void DisposedCacheRejectsNewAcquisitions()
    {
        var cache = new ResidencyCache<int, RecordingDisposable>(_ => new RecordingDisposable());
        var lease = cache.Acquire(1);

        cache.Dispose();

        Should.Throw<ObjectDisposedException>(() => cache.Acquire(2));
        lease.Dispose();
    }

    [Test]
    public async Task DisposalDuringMaterializationDisposesLateValueExactlyOnce()
    {
        var factoryEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var value = new RecordingDisposable();
        var cache = new ResidencyCache<int, RecordingDisposable>(_ =>
        {
            factoryEntered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            return value;
        });

        var acquire = Task.Run(() => cache.Acquire(1));
        await factoryEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cache.Dispose();
        release.TrySetResult();

        await Should.ThrowAsync<ObjectDisposedException>(async () => await acquire);
        value.DisposeCount.ShouldBe(1);

        cache.Dispose();
        value.DisposeCount.ShouldBe(1);
    }

    [Test]
    public void PublishingAfterRetirementWithOtherPinsActiveDefersDisposal()
    {
        var value = new RecordingDisposable();
        var cache = new ResidencyCache<int, RecordingDisposable>(_ => value);
        var entry = new ResidencyCache<int, RecordingDisposable>.Entry(cache, 1);

        entry.TryPin().ShouldBeTrue();
        entry.Publish().ShouldBeSameAs(value);

        entry.TryPin().ShouldBeTrue();
        entry.Retire();

        entry.Publish().ShouldBeNull();
        value.DisposeCount.ShouldBe(0);

        entry.Unpin();
        value.DisposeCount.ShouldBe(0);

        entry.Unpin();
        value.DisposeCount.ShouldBe(1);
    }

    [Test]
    public void MultipleConcurrentLeasesDeferDisposalUntilFinalRelease()
    {
        var value = new RecordingDisposable();
        var cache = new ResidencyCache<int, RecordingDisposable>(_ => value);

        var first = cache.Acquire(1);
        var second = cache.Acquire(1);
        var third = cache.Acquire(1);

        cache.Dispose();
        value.DisposeCount.ShouldBe(0);

        first.Dispose();
        second.Dispose();
        value.DisposeCount.ShouldBe(0);

        third.Dispose();
        value.DisposeCount.ShouldBe(1);
    }

    [Test]
    public void CapacityEvictionNeverDisposesPinnedEntry()
    {
        var values = new Dictionary<int, RecordingDisposable>();
        var cache = new ResidencyCache<int, RecordingDisposable>(
            key =>
            {
                var value = new RecordingDisposable();
                values[key] = value;
                return value;
            },
            capacity: 1
        );

        var first = cache.Acquire(1);
        var second = cache.Acquire(2);

        cache.Trim();
        values[1].DisposeCount.ShouldBe(0);
        values[2].DisposeCount.ShouldBe(0);
        cache.Count.ShouldBe(2);

        first.Dispose();
        cache.Trim();
        values[1].DisposeCount.ShouldBe(1);
        values[2].DisposeCount.ShouldBe(0);
        cache.Count.ShouldBe(1);

        second.Dispose();
        cache.Dispose();
        values[2].DisposeCount.ShouldBe(1);
    }

    [Test]
    public void IdleEvictionNeverDisposesPinnedEntry()
    {
        var value = new RecordingDisposable();
        var cache = new ResidencyCache<int, RecordingDisposable>(
            _ => value,
            idleTimeout: TimeSpan.Zero,
            capacity: 1
        );

        var lease = cache.Acquire(1);
        cache.Trim();

        value.DisposeCount.ShouldBe(0);
        cache.Count.ShouldBe(1);

        lease.Dispose();
        cache.Trim();
        value.DisposeCount.ShouldBe(1);
        cache.Count.ShouldBe(0);
    }

    [Test]
    public async Task ConcurrentAcquireAndDisposeNeverExposesDisposedValue()
    {
        for (var iteration = 0; iteration < 64; iteration++)
        {
            var value = new RecordingDisposable();
            var cache = new ResidencyCache<int, RecordingDisposable>(_ => value);
            var leases = new System.Collections.Concurrent.ConcurrentBag<ResidencyCache<
                int,
                RecordingDisposable
            >.Lease>();
            leases.Add(cache.Acquire(1));

            var worker = Task.Run(() =>
            {
                for (var index = 0; index < 64; index++)
                {
                    try
                    {
                        leases.Add(cache.Acquire(1));
                    }
                    catch (ObjectDisposedException) { }
                }
            });

            var disposer = Task.Run(cache.Dispose);
            await Task.WhenAll(worker, disposer);

            foreach (var lease in leases)
            {
                value.DisposeCount.ShouldBe(0);
                lease.Dispose();
            }

            cache.Dispose();
            value.DisposeCount.ShouldBe(1);
        }
    }

    [Test]
    public async Task DisposeBetweenPublicationAndLeaseReturn_KeepsEscapingValueAliveUntilRelease()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var value = new RecordingDisposable();
        using var cache = new ResidencyCache<int, RecordingDisposable>(_ => value)
        {
            BeforeLeaseReturn = () =>
            {
                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            },
        };
        var acquiring = Task.Run(() => cache.Acquire(1));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cache.Dispose();
            cache.Count.ShouldBe(0);
            value.DisposeCount.ShouldBe(0);
            Should.Throw<ObjectDisposedException>(() => cache.Acquire(1));
        }
        finally
        {
            release.TrySetResult();
        }
        using var lease = await acquiring.WaitAsync(TimeSpan.FromSeconds(5));
        lease.Value.ShouldBeSameAs(value);
        value.DisposeCount.ShouldBe(0);
        lease.Dispose();
        value.DisposeCount.ShouldBe(1);
    }

    [Test]
    public void DisposeFailure_DoesNotPreventRetiringOtherEntries()
    {
        var values = new[]
        {
            new RecordingDisposable { Throws = true },
            new RecordingDisposable(),
            new RecordingDisposable(),
        };
        var cache = new ResidencyCache<int, RecordingDisposable>(key => values[key]);
        cache.Acquire(0).Dispose();
        cache.Acquire(1).Dispose();
        var active = cache.Acquire(2);
        Should.Throw<AggregateException>(cache.Dispose);
        values[0].DisposeCount.ShouldBe(1);
        values[1].DisposeCount.ShouldBe(1);
        values[2].DisposeCount.ShouldBe(0);
        active.Dispose();
        values[2].DisposeCount.ShouldBe(1);
        cache.Dispose();
        values[0].DisposeCount.ShouldBe(1);
    }

    [Test]
    public void EvictionDisposalFailure_ReleasesAcquisitionPin()
    {
        var evicted = new RecordingDisposable { Throws = true };
        var created = new RecordingDisposable();
        var cache = new ResidencyCache<int, RecordingDisposable>(
            key => key == 0 ? evicted : created,
            capacity: 1
        );
        cache.Acquire(0).Dispose();
        Should.Throw<AggregateException>(() => cache.Acquire(1));
        cache.Count.ShouldBe(0);
        using var lease = cache.Acquire(1);
        cache.Dispose();
        created.DisposeCount.ShouldBe(0);
        lease.Dispose();
        created.DisposeCount.ShouldBe(1);
        evicted.DisposeCount.ShouldBe(1);
    }

    private sealed class RecordingDisposable : IDisposable
    {
        private int _disposeCount;
        public bool Throws { get; init; }

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCount);
            if (Throws)
                throw new InvalidOperationException("Value cleanup failed.");
        }
    }
}
