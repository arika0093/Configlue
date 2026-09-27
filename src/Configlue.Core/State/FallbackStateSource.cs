using System.Text;

namespace Configlue;

/// <summary>
/// Combines ordered representations of one logical state and exposes only the first successful representation.
/// </summary>
/// <typeparam name="T">The state type stored by each representation.</typeparam>
/// <remarks>
/// Unlike adding every candidate directly to an options source set, this class does not overlay older
/// representations with the selected value. Its watcher observes the selected representation and higher
/// priority candidates so a recovered higher-priority representation can become active again. Writes update
/// the active writable representation unless a fixed candidate is configured.
/// State is copied to another candidate only when <see cref="PromoteAsync"/> is called. Promotion does not
/// delete other representations. Candidate resources remain owned by the caller.
/// </remarks>
public sealed class FallbackStateSource<T> : IStateReader<T>, IStateWriter<T>, IStateWatcher
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
        if (writeSourceId is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(writeSourceId);
            var writeSource = candidates.Sources.FirstOrDefault(source =>
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
    /// Creates one logical source for an options source set. The logical source keeps the candidate set as an
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
        CancellationToken cancellationToken = default
    )
    {
        var result = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return result.Status == StateReadStatus.Success
            ? result with
            {
                Revision = CreateRevisionToken(result),
            }
            : result;
    }

    /// <inheritdoc />
    public async ValueTask<StateWriteResult> WriteAsync(
        StateWriteRequest<T> request,
        CancellationToken cancellationToken = default
    )
    {
        var current = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (
            (request.CheckRevision || request.ExpectedRevision is not null)
            && !string.Equals(
                request.ExpectedRevision,
                GetRevisionToken(current),
                StringComparison.Ordinal
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
        var targetState = string.Equals(current.SourceId, target.Id, StringComparison.Ordinal)
            ? current
            : await target.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (targetState.Status == StateReadStatus.Unavailable)
        {
            throw new InvalidOperationException(
                $"Cannot safely write fallback state because target '{target.Id}' is unavailable."
            );
        }

        return await target
            .Writer!.WriteAsync(
                new StateWriteRequest<T>(request.Value, targetState.Revision, CheckRevision: true),
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <summary>Copies the selected state into a missing, higher-priority writable candidate.</summary>
    /// <param name="targetSourceId">The candidate to promote into.</param>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    /// <returns>The source and target revisions, or an already-promoted result when the target is active.</returns>
    /// <remarks>
    /// The target must precede the selected candidate in read priority and must return <see cref="StateReadStatus.NotFound"/>.
    /// Its write is conditional on the observed missing-state revision. The selected source and the resolution
    /// are re-read before writing, so detected changes fail with <see cref="StateConflictException"/>. This is
    /// not a transaction across resources: a source can still change immediately after the final check.
    /// </remarks>
    public async ValueTask<StateFallbackPromotionResult> PromoteAsync(
        string targetSourceId,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetSourceId);
        var targetIndex = FindSourceIndex(targetSourceId);
        var target = _candidates.Sources[targetIndex];
        if (target.Writer is null)
        {
            throw new ArgumentException(
                $"Target source '{targetSourceId}' does not support writes.",
                nameof(targetSourceId)
            );
        }

        var current = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (current.Status != StateReadStatus.Success || current.SourceId is null)
        {
            throw new InvalidOperationException(
                "Cannot promote fallback state because no candidate currently supplies a value."
            );
        }

        if (string.Equals(current.SourceId, target.Id, StringComparison.Ordinal))
        {
            return new StateFallbackPromotionResult(
                target.Id,
                target.Id,
                current.Revision,
                current.Revision,
                wasAlreadyPromoted: true
            );
        }

        var sourceIndex = FindSourceIndex(current.SourceId);
        if (targetIndex >= sourceIndex)
        {
            throw new ArgumentException(
                $"Target source '{targetSourceId}' must have higher read priority than the selected source '{current.SourceId}'.",
                nameof(targetSourceId)
            );
        }

        var targetState = await target.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (targetState.Status == StateReadStatus.Unavailable)
        {
            throw new InvalidOperationException(
                $"Cannot promote fallback state because target '{target.Id}' is unavailable."
            );
        }

        if (targetState.Status == StateReadStatus.Success)
        {
            var latest = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (latest.Status == StateReadStatus.Success && latest.SourceId == target.Id)
            {
                return new StateFallbackPromotionResult(
                    target.Id,
                    target.Id,
                    latest.Revision,
                    targetState.Revision,
                    wasAlreadyPromoted: true
                );
            }

            throw new StateConflictException(
                $"Target source '{target.Id}' already contains state and is not the selected candidate."
            );
        }

        var latestResolution = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (
            latestResolution.Status != StateReadStatus.Success
            || !string.Equals(latestResolution.SourceId, current.SourceId, StringComparison.Ordinal)
            || !string.Equals(
                CreateRevisionToken(latestResolution),
                CreateRevisionToken(current),
                StringComparison.Ordinal
            )
        )
        {
            throw new StateConflictException(
                "The selected fallback state changed before promotion."
            );
        }

        var source = _candidates.Sources[sourceIndex];
        var latestSourceState = await source
            .Reader.ReadAsync(cancellationToken)
            .ConfigureAwait(false);
        if (
            latestSourceState.Status != StateReadStatus.Success
            || !string.Equals(
                latestSourceState.Revision,
                current.Revision,
                StringComparison.Ordinal
            )
        )
        {
            throw new StateConflictException(
                "The selected fallback source changed before promotion."
            );
        }

        var write = await target
            .Writer.WriteAsync(
                new StateWriteRequest<T>(current.Value!, targetState.Revision, CheckRevision: true),
                cancellationToken
            )
            .ConfigureAwait(false);

        var confirmedTarget = await target
            .Reader.ReadAsync(cancellationToken)
            .ConfigureAwait(false);
        if (
            confirmedTarget.Status != StateReadStatus.Success
            || (
                write.Revision is not null
                && confirmedTarget.Revision is not null
                && !string.Equals(
                    write.Revision,
                    confirmedTarget.Revision,
                    StringComparison.Ordinal
                )
            )
        )
        {
            throw new StateConflictException(
                $"Target source '{target.Id}' did not retain the promoted state at the written revision."
            );
        }

        return new StateFallbackPromotionResult(
            source.Id,
            target.Id,
            latestSourceState.Revision,
            confirmedTarget.Revision ?? write.Revision,
            wasAlreadyPromoted: false
        );
    }

    /// <inheritdoc />
    public ValueTask WaitForChangeAsync(
        string? observedRevision,
        CancellationToken cancellationToken = default
    ) => _watcher.WaitForChangeAsync(observedRevision, cancellationToken);

    private StateSource<T> ResolveWriteSource(StateReadResult<T> current)
    {
        if (_writeSourceId is { } writeSourceId)
        {
            return _candidates.Sources.First(source =>
                string.Equals(source.Id, writeSourceId, StringComparison.Ordinal)
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

    private int FindSourceIndex(string sourceId)
    {
        for (var index = 0; index < _candidates.Sources.Count; index++)
        {
            if (string.Equals(_candidates.Sources[index].Id, sourceId, StringComparison.Ordinal))
            {
                return index;
            }
        }

        throw new ArgumentException(
            $"Source '{sourceId}' is not registered among the fallback candidates.",
            nameof(sourceId)
        );
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
