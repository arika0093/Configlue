# Synchronous boundaries

Core source reads and writes remain asynchronous. Resolve an options handle from DI, then await GetValueAsync or a write operation. DI does not create a model snapshot by blocking on source I/O.

Synchronous waits are confined to explicit boundaries:

- IDisposable, synchronous registry removal/clear, and construction-failure cleanup finish owned resources before returning. Prefer DisposeAsync, TryRemoveAsync, and ClearAsync when resources perform asynchronous work.
- OpenEditSession is an explicitly blocking convenience for simple synchronous applications. Async callers use OpenEditSessionAsync.
- The optional Microsoft Options adapter implements synchronous framework getters. IOptions and IOptionsSnapshot retain their snapshot semantics; IOptionsMonitor follows notifications. Remote configuration consumers should prefer Configlue's asynchronous options interfaces.
- Synchronous registry operations wait for notifications according to their documented reentrancy and notification-deferral rules.

Profile subscriptions initialize and switch asynchronously. OnChange returns before initialization finishes and does not send an initial value. Read GetActiveValueAsync explicitly for initial state. New selections invalidate older bindings; newer source callbacks supersede pending switch reads. Disposal detaches listeners, cancels reads, and ignores late results. The profile manager's DisposeAsync waits for its outstanding subscription operations. Cancellation requires source cooperation; a custom source that ignores cancellation must eventually return before asynchronous disposal can finish.

RegisterAsSingleton has been removed: resolving a service no longer blocks on source I/O to construct a direct model snapshot. Inject IReadOnlyOptions<T> and await GetValueAsync for live reads. If a fixed startup snapshot is needed, explicitly await configuration during application initialization and pass the materialized model to consumers. Microsoft Options remains an optional framework adapter with explicit synchronous snapshot/live getter semantics.

Callbacks run outside subscription locks. An already dispatched callback may finish during disposal; queued notifications and late source results are suppressed. Owner disposal does not wait for user callbacks, which permits a callback to dispose its owner.
