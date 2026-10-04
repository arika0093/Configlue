using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Configlue;

/// <summary>Executes one check and exposes both its per-source stream and its final result.</summary>
/// <remarks>Advanced application API for operational checks.</remarks>
/// <remarks>
/// One operation represents a single check execution shared by the source stream and <see cref="Result"/>.
/// The underlying sources are read exactly once. The source stream may be enumerated at most once;
/// awaiting <see cref="Result"/> first, or not enumerating the stream at all, is supported and does not
/// start a second read. Abandoning enumeration early still lets <see cref="Result"/> complete.
/// Cancellation surfaces as cancellation rather than as an ordinary result.
/// </remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public sealed class ConfiglueCheckOperation : IAsyncEnumerable<ConfiglueSourceCheckResult>
{
    private readonly ConfiglueCheckRunner _runner;
    private readonly CancellationToken _cancellationToken;
    private readonly Channel<ConfiglueSourceCheckResult> _sources =
        Channel.CreateUnbounded<ConfiglueSourceCheckResult>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true }
        );
    private readonly TaskCompletionSource<ConfiglueCheckResult> _result = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    private readonly object _gate = new();
    private Task? _execution;
    private bool _enumeratorTaken;

    internal ConfiglueCheckOperation(
        ConfiglueCheckRunner runner,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
        _cancellationToken = cancellationToken;
    }

    /// <summary>The final state-resolution result of this check.</summary>
    public ValueTask<ConfiglueCheckResult> Result
    {
        get
        {
            EnsureStarted();
            return new ValueTask<ConfiglueCheckResult>(_result.Task);
        }
    }

    /// <summary>Streams the sources that were evaluated by this check in resolution order.</summary>
    public IAsyncEnumerator<ConfiglueSourceCheckResult> GetAsyncEnumerator(
        CancellationToken cancellationToken = default
    )
    {
        lock (_gate)
        {
            if (_enumeratorTaken)
            {
                throw new InvalidOperationException(
                    "A Configlue check operation's source results can be enumerated only once."
                );
            }

            _enumeratorTaken = true;
        }

        EnsureStarted();
        return new CheckEnumerator(this, cancellationToken);
    }

    private void EnsureStarted()
    {
        lock (_gate)
        {
            _execution ??= ExecuteAsync();
        }
    }

    private async Task ExecuteAsync()
    {
        try
        {
            var result = await _runner(Report, _cancellationToken).ConfigureAwait(false);
            _sources.Writer.TryComplete();
            _result.TrySetResult(result);
        }
        catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested)
        {
            _sources.Writer.TryComplete();
            _result.TrySetCanceled(_cancellationToken);
        }
        catch (Exception exception)
        {
            _sources.Writer.TryComplete();
            _result.TrySetResult(ConfiglueCheckResult.Faulted(exception));
        }
    }

    private void Report(ConfiglueSourceCheckResult result) => _sources.Writer.TryWrite(result);

    private sealed class CheckEnumerator(
        ConfiglueCheckOperation owner,
        CancellationToken cancellationToken
    ) : IAsyncEnumerator<ConfiglueSourceCheckResult>
    {
        private ConfiglueSourceCheckResult _current = null!;

        public ConfiglueSourceCheckResult Current => _current;

        public async ValueTask<bool> MoveNextAsync()
        {
            if (
                !await owner
                    ._sources.Reader.WaitToReadAsync(cancellationToken)
                    .ConfigureAwait(false)
            )
            {
                return false;
            }

            if (owner._sources.Reader.TryRead(out var result))
            {
                _current = result;
                return true;
            }

            return false;
        }

        public ValueTask DisposeAsync() => default;
    }
}

internal delegate Task<ConfiglueCheckResult> ConfiglueCheckRunner(
    Action<ConfiglueSourceCheckResult> reportSource,
    CancellationToken cancellationToken
);
