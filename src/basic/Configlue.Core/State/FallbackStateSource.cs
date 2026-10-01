using System.Text;
using Configlue.Sources;

namespace Configlue.State;

/// <summary>
/// Combines ordered representations of one logical state and exposes only the first successful representation.
/// </summary>
/// <typeparam name="T">The state type stored by each representation.</typeparam>
/// <remarks>
/// Unlike adding every candidate directly to a state source set, this class does not overlay older
/// representations with the selected value. Its watcher observes the selected representation and higher
/// priority candidates so a recovered higher-priority representation can become active again. Writes update
/// the active writable representation unless a fixed candidate is configured. Candidate resources remain
/// owned by the caller.
/// </remarks>
public sealed class FallbackStateSource<T> : ISourceReader<T>, ISourceWriter<T>, ISourceWatcher
{
    private readonly StateSourceSet<T> _candidates;
    private readonly StateSourceResolver<T> _reader;
    private readonly StateSourceWatcher<T> _watcher;
    private readonly string? _writeSourceId;

    /// <summary>Creates a first-available source from ordered candidate representations.</summary>
    /// <param name="candidates">Representations ordered by their priority and registration order.</param>
    /// <param name="writeSourceId">
    /// An optional candidate ID that receives writes. When omitted, writes target the active writable candidate,
    /// falling back to the highest-priority writable candidate when the state is not currently readable.
    /// </param>
    public FallbackStateSource(StateSourceSet<T> candidates, string? writeSourceId = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        StateSource<T>? writeSource = null;
        if (writeSourceId is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(writeSourceId);
            writeSource = candidates.Sources.FirstOrDefault(source =>
                string.Equals(source.Id, writeSourceId, StringComparison.Ordinal)
            );
            if (writeSource is null)
            {
                throw new ArgumentException(
                    $"Write source '{writeSourceId}' is not registered among the fallback candidates.",
                    nameof(writeSourceId)
                );
            }

            if (writeSource.Writer is null)
            {
                throw new ArgumentException(
                    $"Write source '{writeSourceId}' does not support writes.",
                    nameof(writeSourceId)
                );
            }
        }

        _candidates = candidates;
        _reader = new StateSourceResolver<T>(candidates);
        _watcher = new StateSourceWatcher<T>(_reader);
        _writeSourceId = writeSourceId;
    }

    /// <summary>The candidate that supplied the value in the most recent successful read, if any.</summary>
    public StateSource<T>? SelectedSource => _reader.ActiveSource;

    /// <summary>
    /// Creates one logical source for a state source set. The logical source keeps the candidate set as an
    /// alternative representation group rather than merging every candidate as an independent contribution.
    /// </summary>
    public StateSource<T> CreateSource(
        string id,
        int priority = 0,
        StateFallbackCondition fallbackCondition = StateFallbackCondition.NotFound,
        string? physicalOrigin = null
    ) =>
        new(
            id,
            this,
            priority,
            fallbackCondition,
            _candidates.Sources.Any(static source => source.Writer is not null) ? this : null,
            this,
            physicalOrigin
        );

    /// <inheritdoc />
    public async ValueTask<StateReadResult<T>> ReadAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        var result = await _reader.ReadAsync(context, cancellationToken).ConfigureAwait(false);
        if (result.Status != StateReadStatus.Success)
        {
            return result;
        }

        return result with
        {
            Revision = CreateRevisionToken(result),
        };
    }

    /// <inheritdoc />
    public async ValueTask<StateWriteResult> WriteAsync(
        ConfiglueResourceContext context,
        StateWriteRequest<T> request,
        CancellationToken cancellationToken = default
    )
    {
        var current = await _reader.ReadAsync(context, cancellationToken).ConfigureAwait(false);
        return await WriteCoreAsync(context, current, request, cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<StateWriteResult> WriteCoreAsync(
        ConfiglueResourceContext context,
        StateReadResult<T> current,
        StateWriteRequest<T> request,
        CancellationToken cancellationToken
    )
    {
        if (
            !request.Condition.IsSatisfiedBy(
                GetRevisionToken(current),
                current.Status != StateReadStatus.NotFound
            )
        )
        {
            throw new StateConflictException(
                "The selected fallback state changed after it was read."
            );
        }

        if (current.Status == StateReadStatus.Unavailable)
        {
            throw new InvalidOperationException(
                "Cannot safely write fallback state because the current resolution is unavailable."
            );
        }

        var target = ResolveWriteSource(current);
        var targetContext = ReferenceEquals(
            context.Subject,
            ConfiglueResourceContext.DefaultSubject
        )
            ? context
            : target.GetResourceContext(context.Subject);
        StateReadResult<T> targetState;
        if (string.Equals(current.SourceId, target.Id, StringComparison.Ordinal))
        {
            targetState = current;
        }
        else
        {
            targetState = await target
                .ReadAsync(targetContext, cancellationToken)
                .ConfigureAwait(false);
        }
        if (targetState.Status == StateReadStatus.Unavailable)
        {
            throw new InvalidOperationException(
                $"Cannot safely write fallback state because target '{target.Id}' is unavailable."
            );
        }

        var targetRequest = new StateWriteRequest<T>(
            request.Value,
            Condition: RevisionCondition.FromRevision(targetState.Revision)
        );
        return await target
            .WriteAsync(targetContext, targetRequest, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask WaitForChangeAsync(
        ConfiglueResourceContext context,
        string? observedRevision,
        CancellationToken cancellationToken = default
    ) => _watcher.WaitForChangeAsync(context, observedRevision, cancellationToken);

    private StateSource<T> ResolveWriteSource(StateReadResult<T> current)
    {
        if (_writeSourceId is not null)
        {
            return _candidates.Sources.First(source =>
                string.Equals(source.Id, _writeSourceId, StringComparison.Ordinal)
            );
        }

        if (
            current.Status == StateReadStatus.Success
            && _reader.ActiveSource is { Writer: not null } active
        )
        {
            return active;
        }

        return _candidates.Sources.FirstOrDefault(static source => source.Writer is not null)
            ?? throw new InvalidOperationException("No fallback representation supports writes.");
    }

    private static string? GetRevisionToken(StateReadResult<T> result) =>
        result.Status == StateReadStatus.Success ? CreateRevisionToken(result) : result.Revision;

    private static string CreateRevisionToken(StateReadResult<T> result)
    {
        var builder = new StringBuilder();
        AppendPart(builder, "configlue-fallback-state-v1");
        AppendPart(
            builder,
            ((int)result.Status).ToString(System.Globalization.CultureInfo.InvariantCulture)
        );
        AppendPart(builder, result.SourceId);
        AppendPart(builder, result.Revision);
        AppendRevisionVector(builder, result.Revisions);
        return builder.ToString();
    }

    private static void AppendRevisionVector(StringBuilder builder, StateRevisionVector? revisions)
    {
        if (revisions is null)
        {
            AppendPart(builder, "null-vector");
            return;
        }

        AppendPart(builder, "vector");
        AppendPart(
            builder,
            revisions.Revisions.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
        );
        foreach (
            var (sourceId, revision) in revisions.Revisions.OrderBy(
                static item => item.Key,
                StringComparer.Ordinal
            )
        )
        {
            AppendPart(builder, sourceId);
            AppendPart(builder, revision);
        }

        AppendPart(
            builder,
            revisions.NestedRevisions.Count.ToString(
                System.Globalization.CultureInfo.InvariantCulture
            )
        );
        foreach (
            var (sourceId, nested) in revisions.NestedRevisions.OrderBy(
                static item => item.Key,
                StringComparer.Ordinal
            )
        )
        {
            AppendPart(builder, sourceId);
            AppendRevisionVector(builder, nested);
        }
    }

    private static void AppendPart(StringBuilder builder, string? value)
    {
        if (value is null)
        {
            builder.Append("-1:");
            return;
        }

        builder.Append(value.Length).Append(':').Append(value);
    }
}
