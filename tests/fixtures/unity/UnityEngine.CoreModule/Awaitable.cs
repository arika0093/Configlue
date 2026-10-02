using System.Runtime.CompilerServices;

namespace UnityEngine;

/// <summary>
/// Minimal stand-in for Unity's frame-aware awaitable. Completion models whether the
/// captured execution context is the Unity main thread.
/// </summary>
public readonly struct Awaitable
{
    private readonly bool _isMainThread;

    private Awaitable(bool isMainThread) => _isMainThread = isMainThread;

    /// <summary>Whether <see cref="MainThreadAsync"/> should complete synchronously.</summary>
    public static bool IsMainThread { get; set; } = true;

    /// <summary>Returns an awaitable that completes on the Unity main thread.</summary>
    public static Awaitable MainThreadAsync() => new(IsMainThread);

    /// <summary>Returns the awaiter used by the production adapter to test main-thread access.</summary>
    public Awaiter GetAwaiter() => new(_isMainThread);

    /// <summary>The awaiter for <see cref="Awaitable"/>.</summary>
    public readonly struct Awaiter(bool isMainThread) : INotifyCompletion
    {
        /// <summary>Whether the main-thread continuation can run synchronously.</summary>
        public bool IsCompleted => isMainThread;

        /// <summary>Completes the await.</summary>
        public void GetResult() { }

        /// <summary>Schedules the continuation.</summary>
        public void OnCompleted(Action continuation) => continuation();
    }
}
