using System.Diagnostics;

namespace Configlue.Internal;

/// <summary>
/// Caches owned, disposable resources keyed by identity and disposes them deterministically.
/// </summary>
/// <remarks>
/// An entry is leased for the duration of one caller operation so idle eviction and disposal never tear
/// down a resource that is still in use. Materialization and disposal share one lifecycle protocol: a
/// value materialized while the cache is disposed is disposed by its materializer and never escapes, and
/// a value published to the cache is disposed exactly once. Route keys are only retained while an entry
/// is resident; residency is bounded by an idle timeout and a capacity high-water mark.
/// </remarks>
internal sealed class ResidencyCache<TKey, TValue> : IDisposable
    where TKey : notnull
    where TValue : class, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<TKey, Entry> _entries;
    private readonly Func<TKey, TValue> _factory;
    private readonly long _idleTicks;
    private readonly int _capacity;
    private long _lastSweepTimestamp;
    private bool _disposed;

    public ResidencyCache(
        Func<TKey, TValue> factory,
        IEqualityComparer<TKey>? comparer = null,
        TimeSpan? idleTimeout = null,
        int capacity = 256
    )
    {
        ArgumentNullException.ThrowIfNull(factory);
        var timeout = idleTimeout ?? TimeSpan.FromMinutes(5);
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(idleTimeout));
        }

        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _factory = factory;
        _entries = new Dictionary<TKey, Entry>(comparer);
        _idleTicks = (long)(timeout.TotalMilliseconds * Stopwatch.Frequency / 1000.0);
        _capacity = capacity;
        _lastSweepTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>The number of resident entries, for diagnostics and tests.</summary>
    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// Resolves the value for <paramref name="key"/>, materializing it once, and pins it until the returned
    /// lease is disposed.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The cache is disposed.</exception>
    public Lease Acquire(TKey key)
    {
        Entry entry;
        List<Entry>? evicted = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(key, out entry!))
            {
                entry = new Entry(this, key);
                _entries.Add(key, entry);
            }

            entry.Pin();
            MaybeSweepLocked(ref evicted);
        }

        DisposeEntries(evicted);

        TValue? value;
        try
        {
            value = entry.Publish();
        }
        catch
        {
            DiscardFailedEntry(key, entry);
            throw;
        }

        if (value is null || IsDisposed())
        {
            lock (_gate)
            {
                entry.Unpin();
            }

            entry.AbortAndDispose();
            throw new ObjectDisposedException(GetType().FullName);
        }

        return new Lease(this, entry, value);
    }

    /// <summary>Forces an idle sweep and enforces the capacity bound, for diagnostics and tests.</summary>
    internal void Trim()
    {
        List<Entry>? evicted = null;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _lastSweepTimestamp = Stopwatch.GetTimestamp();
            SweepLocked(Stopwatch.GetTimestamp(), ref evicted);
        }

        DisposeEntries(evicted);
    }

    /// <summary>Disposes every resident value and prevents further materialization.</summary>
    public void Dispose()
    {
        List<Entry>? entries = null;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            entries = [.. _entries.Values];
            _entries.Clear();
        }

        foreach (var entry in entries)
        {
            entry.AbortAndDispose();
        }
    }

    private bool IsDisposed()
    {
        lock (_gate)
        {
            return _disposed;
        }
    }

    private void DiscardFailedEntry(TKey key, Entry entry)
    {
        lock (_gate)
        {
            entry.Unpin();
            if (
                !entry.IsValueCreated
                && _entries.TryGetValue(key, out var current)
                && ReferenceEquals(current, entry)
            )
            {
                _entries.Remove(key);
            }
        }
    }

    private void Release(Entry entry)
    {
        lock (_gate)
        {
            entry.Unpin();
        }
    }

    private void MaybeSweepLocked(ref List<Entry>? evicted)
    {
        var now = Stopwatch.GetTimestamp();
        var idleElapsed = _idleTicks == 0 || now - _lastSweepTimestamp >= _idleTicks;
        if (!idleElapsed && _entries.Count <= _capacity)
        {
            return;
        }

        _lastSweepTimestamp = now;
        SweepLocked(now, ref evicted);
    }

    private void SweepLocked(long now, ref List<Entry>? evicted)
    {
        var idleKeys = _entries
            .Where(pair => pair.Value.Pins == 0 && now - pair.Value.LastAccess >= _idleTicks)
            .Select(pair => pair.Key)
            .ToList();
        foreach (var key in idleKeys)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                _entries.Remove(key);
                (evicted ??= []).Add(entry);
            }
        }

        if (_entries.Count <= _capacity)
        {
            return;
        }

        var candidates = _entries
            .Where(pair => pair.Value.Pins == 0)
            .Select(pair => pair.Key)
            .ToList();
        candidates.Sort(
            (left, right) => _entries[left].LastAccess.CompareTo(_entries[right].LastAccess)
        );
        var excess = _entries.Count - _capacity;
        foreach (var key in candidates)
        {
            if (excess == 0)
            {
                break;
            }

            if (_entries.TryGetValue(key, out var entry))
            {
                _entries.Remove(key);
                (evicted ??= []).Add(entry);
                excess--;
            }
        }
    }

    private static void DisposeEntries(List<Entry>? entries)
    {
        if (entries is null)
        {
            return;
        }

        foreach (var entry in entries)
        {
            entry.AbortAndDispose();
        }
    }

    internal sealed class Entry
    {
        private readonly object _lifecycleGate = new();
        private readonly Lazy<TValue> _value;
        private long _lastAccess;
        private int _pins;
        private bool _aborted;
        private bool _valueDisposed;

        public Entry(ResidencyCache<TKey, TValue> owner, TKey key)
        {
            _value = new Lazy<TValue>(
                () => owner._factory(key),
                LazyThreadSafetyMode.ExecutionAndPublication
            );
        }

        public int Pins => _pins;

        public long LastAccess => _lastAccess;

        public bool IsValueCreated => _value.IsValueCreated;

        public void Pin()
        {
            _pins++;
            _lastAccess = Stopwatch.GetTimestamp();
        }

        public void Unpin() => _pins--;

        /// <summary>
        /// Returns the materialized value, or <see langword="null"/> when disposal won the lifecycle
        /// transition; in that case the freshly materialized value is disposed before returning.
        /// </summary>
        public TValue? Publish()
        {
            var value = _value.Value;
            bool dispose;
            lock (_lifecycleGate)
            {
                if (!_aborted)
                {
                    return value;
                }

                dispose = !_valueDisposed;
                if (dispose)
                {
                    _valueDisposed = true;
                }
            }

            if (dispose)
            {
                value.Dispose();
            }

            return null;
        }

        /// <summary>Marks the entry dead and disposes its value exactly once.</summary>
        public void AbortAndDispose()
        {
            TValue? value = null;
            lock (_lifecycleGate)
            {
                _aborted = true;
                if (_value.IsValueCreated && !_valueDisposed)
                {
                    _valueDisposed = true;
                    value = _value.Value;
                }
            }

            value?.Dispose();
        }
    }

    internal sealed class Lease : IDisposable
    {
        private readonly ResidencyCache<TKey, TValue> _owner;
        private Entry? _entry;

        internal Lease(ResidencyCache<TKey, TValue> owner, Entry entry, TValue value)
        {
            _owner = owner;
            _entry = entry;
            Value = value;
        }

        public TValue Value { get; }

        public void Dispose()
        {
            var entry = Interlocked.Exchange(ref _entry, null);
            if (entry is not null)
            {
                _owner.Release(entry);
            }
        }
    }
}
