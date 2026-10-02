using System.Diagnostics;

namespace Configlue.Internal;

/// <summary>
/// Caches owned, disposable resources keyed by identity and disposes them deterministically.
/// </summary>
/// <remarks>
/// An entry is leased for the duration of one caller operation so idle eviction and disposal never tear
/// down a resource that is still in use. The entry lifecycle separates retirement (detached from the
/// resident dictionary, rejecting further pins) from physical disposal: a retired entry disposes
/// immediately only when idle, otherwise disposal is deferred until its final lease releases. A value
/// materialized while the cache is disposed is disposed by its materializer and never escapes, and a
/// value published to the cache is disposed exactly once. Route keys are only retained while an entry
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

    // Allows contract tests to stop after publication while the acquisition still owns its pin.
    internal Action? BeforeLeaseReturn { get; set; }

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
            if (!_entries.TryGetValue(key, out entry!) || !entry.TryPin())
            {
                entry = new Entry(this, key);
                _entries[key] = entry;
                entry.TryPin();
            }

            MaybeSweepLocked(ref evicted);
        }

        try
        {
            DisposeEntries(evicted);
            var value = entry.Publish();
            if (value is null || IsDisposed())
            {
                throw new ObjectDisposedException(GetType().FullName);
            }

            BeforeLeaseReturn?.Invoke();
            return new Lease(entry, value);
        }
        catch
        {
            DiscardFailedEntry(key, entry);
            throw;
        }
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

    /// <summary>Retires all entries, disposing idle values and deferring active values until their last release.</summary>
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

        DisposeEntries(entries);
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

        List<Exception>? errors = null;
        foreach (var entry in entries)
        {
            try
            {
                entry.Retire();
            }
            catch (Exception exception)
            {
                (errors ??= []).Add(exception);
            }
        }
        if (errors is not null)
        {
            throw new AggregateException("One or more cached values failed to dispose.", errors);
        }
    }

    internal sealed class Entry
    {
        private readonly object _lifecycleGate = new();
        private readonly Lazy<TValue> _value;
        private long _lastAccess;
        private int _pins;
        private bool _retired;
        private bool _valueDisposed;

        public Entry(ResidencyCache<TKey, TValue> owner, TKey key)
        {
            _value = new Lazy<TValue>(
                () => owner._factory(key),
                LazyThreadSafetyMode.ExecutionAndPublication
            );
        }

        public int Pins
        {
            get
            {
                lock (_lifecycleGate)
                {
                    return _pins;
                }
            }
        }

        public long LastAccess
        {
            get
            {
                lock (_lifecycleGate)
                {
                    return _lastAccess;
                }
            }
        }

        public bool IsValueCreated => _value.IsValueCreated;

        /// <summary>Pins the entry for a lease, or returns <see langword="false"/> when it is retired.</summary>
        public bool TryPin()
        {
            lock (_lifecycleGate)
            {
                if (_retired)
                {
                    return false;
                }

                _pins++;
                _lastAccess = Stopwatch.GetTimestamp();
                return true;
            }
        }

        /// <summary>
        /// Releases one pin and physically disposes the value when this is the final lease of a retired
        /// entry.
        /// </summary>
        public void Unpin()
        {
            TValue? dispose = null;
            lock (_lifecycleGate)
            {
                _pins--;
                if (_pins == 0 && _retired && _value.IsValueCreated && !_valueDisposed)
                {
                    _valueDisposed = true;
                    dispose = _value.Value;
                }
            }

            dispose?.Dispose();
        }

        /// <summary>
        /// Returns the materialized value while the caller's pin keeps it alive, or <see langword="null"/>
        /// when retirement won the lifecycle transition. A losing materialization is disposed by the
        /// release of its pin, preserving the value for any other active lease.
        /// </summary>
        public TValue? Publish()
        {
            var value = _value.Value;
            lock (_lifecycleGate)
            {
                return _retired ? null : value;
            }
        }

        /// <summary>
        /// Detaches the entry and disposes its value immediately when idle, otherwise defers disposal until
        /// the final lease releases.
        /// </summary>
        public void Retire()
        {
            TValue? dispose = null;
            lock (_lifecycleGate)
            {
                _retired = true;
                if (_pins == 0 && _value.IsValueCreated && !_valueDisposed)
                {
                    _valueDisposed = true;
                    dispose = _value.Value;
                }
            }

            dispose?.Dispose();
        }
    }

    internal sealed class Lease : IDisposable
    {
        private Entry? _entry;

        internal Lease(Entry entry, TValue value)
        {
            _entry = entry;
            Value = value;
        }

        public TValue Value { get; }

        public void Dispose()
        {
            var entry = Interlocked.Exchange(ref _entry, null);
            entry?.Unpin();
        }
    }
}
