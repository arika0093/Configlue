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
/// the active writable representation unless a fixed candidate is configured. Optional read promotion
/// copies a selected lower-priority representation into the fixed highest-priority candidate and retains
/// the original representation. Candidate resources remain owned by the caller.
/// </remarks>
public sealed class FallbackStateSource<T> : IStateReader<T>, IStateWriter<T>, IStateWatcher
{
    private readonly StateSourceSet<T> _candidates;
    private readonly StateSourceResolver<T> _reader;
    private readonly StateSourceWatcher<T> _watcher;
    private readonly string? _writeSourceId;
    private readonly bool _promoteOnRead;
    private readonly SemaphoreSlim _promotionGate = new(1, 1);

    /// <summary>Creates a first-available source from ordered candidate representations.</summary>
    /// <param name="candidates">Representations ordered by their priority and registration order.</param>
    /// <param name="writeSourceId">
    /// An optional candidate ID that receives writes. When omitted, writes target the active writable candidate,
    /// falling back to the highest-priority writable candidate when the state is not currently readable.
    /// </param>
    public FallbackStateSource(StateSourceSet<T> candidates, string? writeSourceId = null)
        : this(candidates, writeSourceId, promoteOnRead: false) { }

    /// <summary>Creates a first-available source with optional promotion into a canonical candidate.</summary>
    /// <param name="candidates">Representations ordered by their priority and registration order.</param>
    /// <param name="writeSourceId">The fixed writable candidate that receives writes.</param>
    /// <param name="promoteOnRead">
    /// Whether to copy a selected fallback representation into the fixed highest-priority candidate when it is read.
    /// </param>
    public FallbackStateSource(
        StateSourceSet<T> candidates,
        string? writeSourceId,
        bool promoteOnRead
    )
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

        if (promoteOnRead && writeSource is null)
        {
            throw new ArgumentException(
                "Read promotion requires an explicit writable canonical source.",
                nameof(writeSourceId)
            );
        }

        if (promoteOnRead && !ReferenceEquals(candidates.Sources[0], writeSource))
        {
            throw new ArgumentException(
                "The canonical source must be the highest-priority fallback candidate.",
                nameof(writeSourceId)
            );
        }

        _candidates = candidates;
        _reader = new StateSourceResolver<T>(candidates);
        _watcher = new StateSourceWatcher<T>(_reader);
        _writeSourceId = writeSourceId;
        _promoteOnRead = promoteOnRead;
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
        if (result.Status != StateReadStatus.Success)
        {
            return result;
        }

        result = result with { Revision = CreateRevisionToken(result) };
        if (
            !_promoteOnRead
            || string.Equals(result.SourceId, _writeSourceId, StringComparison.Ordinal)
        )
        {
            return result;
        }

        return await PromoteOnReadAsync(cancellationToken).ConfigureAwait(false);
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

    /// <inheritdoc />
    public ValueTask WaitForChangeAsync(
        string? observedRevision,
        CancellationToken cancellationToken = default
    ) => _watcher.WaitForChangeAsync(observedRevision, cancellationToken);

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

    private async ValueTask<StateReadResult<T>> PromoteOnReadAsync(
        CancellationToken cancellationToken
    )
    {
        await _promotionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var target = ResolveWriteSource(default);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (current.Status != StateReadStatus.Success)
                {
                    return current;
                }

                if (string.Equals(current.SourceId, target.Id, StringComparison.Ordinal))
                {
                    return current with { Revision = CreateRevisionToken(current) };
                }

                var targetState = await target
                    .Reader.ReadAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (targetState.Status == StateReadStatus.Success)
                {
                    continue;
                }

                if (targetState.Status != StateReadStatus.NotFound)
                {
                    throw new InvalidOperationException(
                        $"Cannot promote fallback state because canonical source '{target.Id}' returned {targetState.Status}."
                    );
                }

                var confirmed = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (
                    confirmed.Status != StateReadStatus.Success
                    || !string.Equals(
                        confirmed.SourceId,
                        current.SourceId,
                        StringComparison.Ordinal
                    )
                    || !string.Equals(
                        CreateRevisionToken(confirmed),
                        CreateRevisionToken(current),
                        StringComparison.Ordinal
                    )
                )
                {
                    continue;
                }

                await target
                    .Writer!.WriteAsync(
                        new StateWriteRequest<T>(
                            confirmed.Value!,
                            targetState.Revision,
                            CheckRevision: true
                        ),
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                var promoted = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (
                    promoted.Status == StateReadStatus.Success
                    && string.Equals(promoted.SourceId, target.Id, StringComparison.Ordinal)
                )
                {
                    return promoted with { Revision = CreateRevisionToken(promoted) };
                }

                throw new StateConflictException(
                    $"Fallback state was written to canonical source '{target.Id}', but it did not become the selected representation."
                );
            }

            throw new StateConflictException(
                "Fallback state changed repeatedly while it was being promoted."
            );
        }
        finally
        {
            _promotionGate.Release();
        }
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
