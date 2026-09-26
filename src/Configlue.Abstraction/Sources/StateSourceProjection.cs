namespace Configlue;

/// <summary>Projects a source-specific state contract into a logical model fragment.</summary>
public static class StateSourceProjection
{
    /// <summary>
    /// Adapts a typed source so it contributes a projected value to another source set.
    /// Source-schema migrations should run before projection. <paramref name="projectedSchema"/>, when supplied,
    /// describes the projected value and is passed to the target runtime's migration pipeline.
    /// </summary>
    /// <typeparam name="TSource">The source-specific state type.</typeparam>
    /// <typeparam name="TTarget">The logical state type contributed to the target source set.</typeparam>
    /// <param name="source">The source to adapt.</param>
    /// <param name="toTarget">Projects a source value into the target logical state.</param>
    /// <param name="toSource">Maps writes back to the source-specific state. Omit it to expose a read-only source.</param>
    /// <param name="projectedSchema">Optional schema metadata for the projected target value.</param>
    /// <param name="sourceMigrations">Optional migrations to apply to the source value before projection.</param>
    /// <param name="sourceSchema">The target schema for source migrations.</param>
    public static StateSource<TTarget> Project<TSource, TTarget>(
        StateSource<TSource> source,
        Func<TSource, TTarget> toTarget,
        Func<TTarget, TSource>? toSource = null,
        StateSchemaMetadata? projectedSchema = null,
        IEnumerable<IStateSchemaMigration<TSource>>? sourceMigrations = null,
        StateSchemaMetadata? sourceSchema = null
    )
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(toTarget);

        if (sourceMigrations is not null && sourceSchema is null)
        {
            throw new ArgumentException(
                "A target source schema is required when source migrations are supplied.",
                nameof(sourceSchema)
            );
        }

        var migrationChain = sourceSchema is { } targetSourceSchema
            ? new StateSchemaMigrationChain<TSource>(targetSourceSchema, sourceMigrations)
            : null;
        var reader = new ProjectedReader<TSource, TTarget>(
            source.Reader,
            toTarget,
            projectedSchema,
            migrationChain
        );
        IStateWriter<TTarget>? writer =
            source.Writer is not null && toSource is not null
                ? new ProjectedWriter<TSource, TTarget>(source.Writer, toSource)
                : null;
        return new StateSource<TTarget>(
            source.Id,
            reader,
            source.Priority,
            source.FallbackCondition,
            writer,
            source.Watcher,
            source.PhysicalOrigin,
            source.ResourceId
        );
    }

    private sealed class ProjectedReader<TSource, TTarget>(
        IStateReader<TSource> source,
        Func<TSource, TTarget> toTarget,
        StateSchemaMetadata? projectedSchema,
        StateSchemaMigrationChain<TSource>? migrationChain
    ) : IStateReader<TTarget>
    {
        public async ValueTask<StateReadResult<TTarget>> ReadAsync(
            CancellationToken cancellationToken = default
        )
        {
            var result = await source.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (result.Status != StateReadStatus.Success)
            {
                return new StateReadResult<TTarget>(
                    result.Status,
                    default,
                    result.Revision,
                    result.SourceId,
                    result.PhysicalOrigin,
                    projectedSchema,
                    result.Revisions
                );
            }

            if (result.Value is null)
            {
                throw new InvalidOperationException(
                    "A successful projected source returned a null value."
                );
            }

            var sourceValue = result.Value;
            if (result.Schema is { } sourceSchema && migrationChain is not null)
            {
                sourceValue = await migrationChain
                    .MigrateAsync(sourceValue, sourceSchema, cancellationToken)
                    .ConfigureAwait(false);
            }

            var projected = toTarget(sourceValue);
            if (projected is null)
            {
                throw new InvalidOperationException("The source projection returned a null value.");
            }

            return new StateReadResult<TTarget>(
                StateReadStatus.Success,
                projected,
                result.Revision,
                result.SourceId,
                result.PhysicalOrigin,
                projectedSchema,
                result.Revisions
            );
        }
    }

    private sealed class ProjectedWriter<TSource, TTarget>(
        IStateWriter<TSource> source,
        Func<TTarget, TSource> toSource
    ) : IStateWriter<TTarget>, IStateWriteBatchParticipant<TTarget>
    {
        public ValueTask<StateWriteResult> WriteAsync(
            StateWriteRequest<TTarget> request,
            CancellationToken cancellationToken = default
        ) =>
            source.WriteAsync(
                new StateWriteRequest<TSource>(
                    toSource(request.Value),
                    request.ExpectedRevision,
                    request.CheckRevision
                ),
                cancellationToken
            );

        public bool TryCreateBatchWrite(
            StateWriteRequest<TTarget> request,
            out ResourceId resourceId,
            out IResourceBatchWriter? batchWriter,
            out ResourceWriteMutation? mutation
        )
        {
            if (source is IStateWriteBatchParticipant<TSource> participant)
            {
                return participant.TryCreateBatchWrite(
                    new StateWriteRequest<TSource>(
                        toSource(request.Value),
                        request.ExpectedRevision,
                        request.CheckRevision
                    ),
                    out resourceId,
                    out batchWriter,
                    out mutation
                );
            }

            resourceId = default;
            batchWriter = null;
            mutation = null;
            return false;
        }
    }
}
