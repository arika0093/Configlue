namespace Configlue;

/// <summary>Projects a source-specific state contract into a logical model fragment.</summary>
public static class StateSourceProjection
{
    /// <summary>
    /// Mounts a generated subtree fragment into its matching nested member of a root fragment.
    /// The mounted source is read-only; reverse writes require an explicit binding contract.
    /// </summary>
    /// <typeparam name="TSource">The generated fragment for the nested model.</typeparam>
    /// <typeparam name="TTarget">The generated root fragment.</typeparam>
    /// <param name="source">The nested source to mount.</param>
    /// <param name="propertyPath">The dotted logical path to the nested model.</param>
    /// <param name="projectedSchema">Optional schema metadata for the root fragment.</param>
    public static StateSource<TTarget> Mount<TSource, TTarget>(
        StateSource<TSource> source,
        string propertyPath,
        StateSchemaMetadata? projectedSchema = null
    )
        where TSource : class, IConfiglueFragment<TSource>
        where TTarget : class, IConfiglueFragment<TTarget>
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyPath);
        var path = propertyPath.Split('.', StringSplitOptions.None);
        if (path.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "A mount path cannot contain empty segments.",
                nameof(propertyPath)
            );
        }

        var rootSchema = TTarget.Empty.Schema;
        var sourceSchema = TSource.Empty.Schema;
        _ = CreateMountedFragment(
            rootSchema,
            path,
            0,
            TSource.Empty,
            sourceSchema.ModelType,
            propertyPath
        );
        var mounted = Project(
            source,
            fragment =>
                (TTarget)CreateMountedFragment(
                    rootSchema,
                    path,
                    0,
                    fragment,
                    sourceSchema.ModelType,
                    propertyPath
                ),
            projectedSchema: projectedSchema ?? rootSchema.ToMetadata()
        );
        return mounted;
    }

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

    private static IConfiglueFragment CreateMountedFragment(
        ConfiglueModelSchema targetSchema,
        IReadOnlyList<string> path,
        int pathIndex,
        IConfiglueFragment sourceFragment,
        Type sourceModelType,
        string propertyPath
    )
    {
        var matches = targetSchema
            .Members.Where(member =>
                string.Equals(member.Name, path[pathIndex], StringComparison.OrdinalIgnoreCase)
            )
            .ToArray();
        if (matches.Length != 1)
        {
            throw new ArgumentException(
                $"Mount path '{propertyPath}' has an unknown or ambiguous member '{path[pathIndex]}' in model '{targetSchema.ModelType}'.",
                nameof(propertyPath)
            );
        }

        var member = matches[0];
        var nestedSchema = member.NestedSchemaFactory?.Invoke();
        if (nestedSchema is null)
        {
            throw new ArgumentException(
                $"Mount path '{propertyPath}' continues through non-nested member '{member.Name}'.",
                nameof(propertyPath)
            );
        }

        if (pathIndex == path.Count - 1)
        {
            if (nestedSchema.ModelType != sourceModelType)
            {
                throw new ArgumentException(
                    $"Mount path '{propertyPath}' expects fragment model '{nestedSchema.ModelType}', but source fragment model is '{sourceModelType}'.",
                    nameof(propertyPath)
                );
            }

            return targetSchema.CreateEmptyFragment().WithMember(member.Id, sourceFragment);
        }

        var nestedFragment = CreateMountedFragment(
            nestedSchema,
            path,
            pathIndex + 1,
            sourceFragment,
            sourceModelType,
            propertyPath
        );
        return targetSchema.CreateEmptyFragment().WithMember(member.Id, nestedFragment);
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
