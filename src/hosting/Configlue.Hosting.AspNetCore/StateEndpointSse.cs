using System.Text;
using Configlue.CompilerServices;
using Configlue.Provider.Json;
using Microsoft.AspNetCore.Http;

namespace Configlue.Hosting.AspNetCore;

/// <summary>SSE lifecycle stage: baseline resolution, subscription, event pump.</summary>
internal static class StateEndpointSse
{
    public static void WriteHeaders(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        try
        {
            context.Response.Headers.Connection = "keep-alive";
        }
        catch (InvalidOperationException)
        {
            // HTTP/2 forbids the Connection header; streaming works without it.
        }

        context.Response.Headers["X-Accel-Buffering"] = "no";
    }

    /// <summary>Applies Last-Event-ID convergence over the loaded ETag.</summary>
    public static string ApplyLastEventId(HttpContext context, string currentEtag)
    {
        var lastEventIds = context.Request.Headers["Last-Event-ID"];
        if (
            lastEventIds.Count == 1
            && StateEndpointPreconditions.TryExtractHex(
                StateEndpointPreconditions.FormatEtag(lastEventIds[0] ?? string.Empty),
                out var lastEventId
            )
        )
        {
            return lastEventId;
        }

        return currentEtag;
    }

    /// <summary>
    /// Coalescing change channel: concurrent notifications collapse to the latest ETag
    /// and the pump emits one SSE invalidation per distinct ETag.
    /// </summary>
    public sealed class ChangeChannel<TModel> : IDisposable
        where TModel : IConfiglueFacadeModel<TModel>
    {
        private readonly object _gate = new();
        private string? _pendingEtag;
        private TaskCompletionSource _signal = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private IDisposable? _attached;
        private string _currentEtag;

        private ChangeChannel(string currentEtag) => _currentEtag = currentEtag;

        public static ChangeChannel<TModel> Subscribe(
            IReadOnlyState<TModel> state,
            ConfiglueModelDescriptor<TModel> descriptor,
            ValidatedStateEndpointOptions options,
            string currentEtag
        )
        {
            var channel = new ChangeChannel<TModel>(currentEtag);
            channel.Attach(state, descriptor, options);
            return channel;
        }

        private void Attach(
            IReadOnlyState<TModel> state,
            ConfiglueModelDescriptor<TModel> descriptor,
            ValidatedStateEndpointOptions options
        )
        {
            _attached = state.OnChange(newValue =>
            {
                try
                {
                    var fragment = descriptor.ToFragmentBoxed(newValue);
                    var bytes = ConfiglueFragmentJson.SerializeToCanonicalBytes(
                        fragment,
                        descriptor.FragmentType,
                        options.SerializerOptions
                    );
                    var etag = StateEndpointPreconditions.ComputeEtagHex(bytes);
                    lock (_gate)
                    {
                        if (string.Equals(etag, _currentEtag, StringComparison.Ordinal))
                        {
                            return;
                        }

                        _pendingEtag = etag;
                        _signal.TrySetResult();
                    }
                }
                catch (Exception exception)
                {
                    // Serialization failures must not break the SSE subscription; the next
                    // successful change notification will still converge the client.
                    System.Diagnostics.Debug.WriteLine(exception);
                }
            });
        }

        /// <summary>
        /// Re-reads the state after subscribing so changes during SSE startup are
        /// observed even when they occurred after the client's last GET.
        /// </summary>
        public async Task ConvergeAsync(
            IReadOnlyState<TModel> state,
            ConfiglueModelDescriptor<TModel> descriptor,
            ValidatedStateEndpointOptions options,
            CancellationToken cancellation
        )
        {
            var current = await state.GetValueAsync(cancellation).ConfigureAwait(false);
            var convergedEtag = StateEndpointPreconditions.ComputeEtagHex(
                StateEndpointCodec.EncodeModel(descriptor, current, options.SerializerOptions)
            );
            lock (_gate)
            {
                // A notification received during the read already supplies a pending
                // invalidation; avoid replacing it with a possibly older snapshot.
                if (
                    _pendingEtag is null
                    && !string.Equals(convergedEtag, _currentEtag, StringComparison.Ordinal)
                )
                {
                    _pendingEtag = convergedEtag;
                    _signal.TrySetResult();
                }
            }
        }

        public async Task PumpAsync(HttpContext context, CancellationToken cancellation)
        {
            while (!cancellation.IsCancellationRequested)
            {
                Task signalTask;
                lock (_gate)
                {
                    signalTask = _signal.Task;
                }

                try
                {
                    await signalTask.WaitAsync(cancellation).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                string etagToSend;
                lock (_gate)
                {
                    etagToSend = _pendingEtag ?? _currentEtag;
                    _pendingEtag = null;
                    if (_signal.Task.IsCompleted)
                    {
                        _signal = new TaskCompletionSource(
                            TaskCreationOptions.RunContinuationsAsynchronously
                        );
                    }
                }

                if (string.Equals(etagToSend, _currentEtag, StringComparison.Ordinal))
                {
                    continue;
                }

                _currentEtag = etagToSend;
                var payload = $"event: changed\nid: {etagToSend}\ndata: {{}}\n\n";
                var bytes = Encoding.UTF8.GetBytes(payload);
                try
                {
                    await context
                        .Response.Body.WriteAsync(bytes, cancellation)
                        .ConfigureAwait(false);
                    await context.Response.Body.FlushAsync(cancellation).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException)
                {
                    break;
                }
            }
        }

        public void Dispose() => _attached?.Dispose();
    }
}
