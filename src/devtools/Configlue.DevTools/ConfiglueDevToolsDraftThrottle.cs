namespace Configlue.DevTools;

/// <summary>
/// Debounce gate between Monaco keystrokes and server-side draft synchronization.
/// </summary>
/// <remarks>
/// <para>
/// Development-only. Typing stays browser-local: the component records each content
/// change with <see cref="NoteEdit"/> and only asks the server for the full draft
/// (after debounce) or on explicit Save/Diff/Validate. This type holds the gate
/// decision so tests can prove no per-keypress round-trip without driving a browser.
/// </para>
/// <para>All timestamps are caller-supplied so tests stay deterministic.</para>
/// </remarks>
public sealed class ConfiglueDevToolsDraftThrottle
{
    private DateTimeOffset? _lastEditUtc;
    private DateTimeOffset? _lastSentUtc;

    /// <summary>Creates a throttle with the supplied debounce interval.</summary>
    /// <param name="debounceInterval">How long the draft must be quiet before syncing.</param>
    public ConfiglueDevToolsDraftThrottle(TimeSpan debounceInterval)
    {
        if (debounceInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(debounceInterval));
        }

        DebounceInterval = debounceInterval;
    }

    /// <summary>Creates a throttle with the default debounce interval (600ms).</summary>
    public ConfiglueDevToolsDraftThrottle()
        : this(TimeSpan.FromMilliseconds(600)) { }

    /// <summary>How long the draft must be quiet before a background sync is allowed.</summary>
    public TimeSpan DebounceInterval { get; set; }

    /// <summary>Records one browser-local content change. Never performs I/O.</summary>
    public void NoteEdit(DateTimeOffset nowUtc) => _lastEditUtc = nowUtc;

    /// <summary>
    /// Whether the draft may be sent to the server now.
    /// </summary>
    /// <param name="nowUtc">The current timestamp.</param>
    /// <param name="explicitRequest">
    /// True for Save/Diff/Validate/secret actions, which always synchronize.
    /// </param>
    /// <returns>
    /// True when <paramref name="explicitRequest"/> is set, or when a recorded edit
    /// has been quiet for at least <see cref="DebounceInterval"/> and has not been
    /// sent yet.
    /// </returns>
    public bool ShouldSync(DateTimeOffset nowUtc, bool explicitRequest = false)
    {
        if (explicitRequest)
        {
            return true;
        }

        if (_lastEditUtc is not { } lastEdit)
        {
            return false;
        }

        if (_lastSentUtc is { } lastSent && lastSent >= lastEdit)
        {
            return false;
        }

        return nowUtc - lastEdit >= DebounceInterval;
    }

    /// <summary>Records that the current draft was sent to the server.</summary>
    public void MarkSent(DateTimeOffset nowUtc) => _lastSentUtc = nowUtc;

    /// <summary>Whether an unsent edit is currently pending.</summary>
    public bool HasPendingEdit =>
        _lastEditUtc is not null && (_lastSentUtc is null || _lastSentUtc < _lastEditUtc);
}
