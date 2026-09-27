namespace Configlue;

/// <summary>Projects a source-specific state contract into a logical model fragment.</summary>
public static class StateSourceProjection
{
    /// <summary>
    /// Mounts a generated subtree fragment into its matching nested member of a root fragment.
    /// A writable source is reverse-projected automatically by extracting the sparse nested fragment.
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
        where TTarget : class, IConfiglueFragment<TTarget> =>
        MountCore<TSource, TTarget>(source, propertyPath, projectedSchema, null);

    /// <summary>Mounts a generated subtree fragment with an explicit reverse projection.</summary>
    /// <typeparam name="TSource">The generated fragment for the nested model.</typeparam>
    /// <typeparam name="TTarget">The generated root fragment.</typeparam>
    /// <param name="source">The nested source to mount.</param>
    /// <param name="propertyPath">The dotted logical path to the nested model.</param>
    /// <param name="toSource">Maps the sparse root contribution back to the source fragment.</param>
    /// <param name="projectedSchema">Optional schema metadata for the root fragment.</param>
    public static StateSource<TTarget> Mount<TSource, TTarget>(
        StateSource<TSource> source,
        string propertyPath,
        Func<TTarget, TSource> toSource,
        StateSchemaMetadata? projectedSchema = null
    )
        where TSource : class, IConfiglueFragment<TSource>
        where TTarget : class, IConfiglueFragment<TTarget>
    {
        ArgumentNullException.ThrowIfNull(toSource);
        return MountCore(source, propertyPath, projectedSchema, toSource);
    }

    private static StateSource<TTarget> MountCore<TSource, TTarget>(
        StateSource<TSource> source,
        string propertyPath,
        StateSchemaMetadata? projectedSchema,
        Func<TTarget, TSource>? toSource
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
        var reverseProjection = toSource;
        if (reverseProjection is null && source.Writer is not null)
        {
            reverseProjection = fragment =>
                (TSource)ExtractMountedFragment(rootSchema, path, 0, fragment, propertyPath);
        }

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
            reverseProjection,
            projectedSchema: projectedSchema ?? rootSchema.ToMetadata()
        );
        return mounted;
    }

    private static IConfiglueFragment ExtractMountedFragment(
        ConfiglueModelSchema schema,
        IReadOnlyList<string> path,
        int pathIndex,
        IConfiglueFragment fragment,
        string propertyPath
    )
    {
        var matches = schema
            .Members.Where(member =>
                string.Equals(member.Name, path[pathIndex], StringComparison.OrdinalIgnoreCase)
            )
            .ToArray();
        if (matches.Length != 1)
        {
            throw new ArgumentException(
                $"Mount path '{propertyPath}' has an unknown or ambiguous member '{path[pathIndex]}' in model '{schema.ModelType}'.",
                nameof(propertyPath)
            );
        }

        var member = matches[0];
        var present = fragment
            .EnumeratePresentMembers()
            .Where(candidate => candidate.Id == member.Id)
            .Select(static candidate => (ConfiglueFragmentMember?)candidate)
            .FirstOrDefault();
        if (present is null)
        {
            return GetSubtreeSchema(schema, path, pathIndex, propertyPath).CreateEmptyFragment();
        }

        var presentValue = present.Value.Value;
        if (pathIndex == path.Count - 1)
        {
            if (presentValue is not IConfiglueFragment subtree)
            {
                throw new InvalidOperationException(
                    $"Mounted subtree '{propertyPath}' is present with null or a value that is not a generated fragment, so it cannot be reverse-projected."
                );
            }

            return subtree;
        }

        if (presentValue is not IConfiglueFragment nested)
        {
            throw new InvalidOperationException(
                $"Mounted subtree path '{propertyPath}' contains a present-null member and cannot be reverse-projected."
            );
        }

        return ExtractMountedFragment(
            member.NestedSchemaFactory!(),
            path,
            pathIndex + 1,
            nested,
            propertyPath
        );
    }

    private static ConfiglueModelSchema GetSubtreeSchema(
        ConfiglueModelSchema schema,
        IReadOnlyList<string> path,
        int pathIndex,
        string propertyPath
    )
    {
        var member = schema.Members.SingleOrDefault(candidate =>
            string.Equals(candidate.Name, path[pathIndex], StringComparison.OrdinalIgnoreCase)
        );
        var nested = member.NestedSchemaFactory?.Invoke();
        if (nested is null)
        {
            throw new ArgumentException(
                $"Mount path '{propertyPath}' continues through non-nested member '{member.Name}'.",
                nameof(propertyPath)
            );
        }

        return pathIndex == path.Count - 1
            ? nested
            : GetSubtreeSchema(nested, path, pathIndex + 1, propertyPath);
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
    ) =>
        ProjectCore(
            source,
            toTarget,
            toSource,
            updateSource: null,
            projectedSchema,
            sourceMigrations,
            sourceSchema
        );

    /// <summary>Projects a source and updates its current contract when reverse-mapping writes.</summary>
    /// <remarks>The current-aware writer re-reads the source and asynchronously prepares batch mutations when the source supports batching. The callback must preserve unprojected data and reject unset operations the source cannot represent.</remarks>
    /// <param name="source">The source to adapt.</param>
    /// <param name="toTarget">Projects a source value into the target logical state.</param>
    /// <param name="updateSource">Updates the current source contract from the previous projected value, the new projected value, and the current source contract.</param>
    /// <param name="projectedSchema">Optional schema metadata for the projected target value.</param>
    /// <param name="sourceMigrations">Optional migrations to apply to the source value before projection.</param>
    /// <param name="sourceSchema">The target schema for source migrations.</param>
    public static StateSource<TTarget> ProjectWithUpdate<TSource, TTarget>(
        StateSource<TSource> source,
        Func<TSource, TTarget> toTarget,
        Func<TTarget?, TTarget, TSource?, TSource> updateSource,
        StateSchemaMetadata? projectedSchema = null,
        IEnumerable<IStateSchemaMigration<TSource>>? sourceMigrations = null,
        StateSchemaMetadata? sourceSchema = null
    ) =>
        ProjectCore(
            source,
            toTarget,
            toSource: null,
            updateSource,
            projectedSchema,
            sourceMigrations,
            sourceSchema
        );

    private static StateSource<TTarget> ProjectCore<TSource, TTarget>(
        StateSource<TSource> source,
        Func<TSource, TTarget> toTarget,
        Func<TTarget, TSource>? toSource,
        Func<TTarget?, TTarget, TSource?, TSource>? updateSource,
        StateSchemaMetadata? projectedSchema,
        IEnumerable<IStateSchemaMigration<TSource>>? sourceMigrations,
        StateSchemaMetadata? sourceSchema
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
            source.Writer is not null && (toSource is not null || updateSource is not null)
                ? new ProjectedWriter<TSource, TTarget>(
                    source.Reader,
                    source.Writer,
                    toTarget,
                    toSource,
                    updateSource,
                    migrationChain
                )
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
        IStateReader<TSource> reader,
        IStateWriter<TSource> source,
        Func<TSource, TTarget> toTarget,
        Func<TTarget, TSource>? toSource,
        Func<TTarget?, TTarget, TSource?, TSource>? updateSource,
        StateSchemaMigrationChain<TSource>? migrationChain
    )
        : IStateWriter<TTarget>,
            IStateWriteBatchParticipant<TTarget>,
            IAsyncStateWriteBatchParticipant<TTarget>
    {
        public bool CanPrepareBatchWrite =>
            source is IStateWriteBatchParticipant<TSource>
            || source is IAsyncStateWriteBatchParticipant<TSource> { CanPrepareBatchWrite: true };

        public async ValueTask<StateWriteResult> WriteAsync(
            StateWriteRequest<TTarget> request,
            CancellationToken cancellationToken = default
        )
        {
            var mapped = updateSource is null
                ? toSource!(request.Value)
                : await UpdateSourceAsync(request, cancellationToken).ConfigureAwait(false);
            if (mapped is null)
            {
                throw new InvalidOperationException("The source projection returned a null value.");
            }
            return await source
                .WriteAsync(
                    new StateWriteRequest<TSource>(
                        mapped,
                        request.ExpectedRevision,
                        request.CheckRevision
                    ),
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        public bool TryCreateBatchWrite(
            StateWriteRequest<TTarget> request,
            out ResourceId resourceId,
            out IResourceBatchWriter? batchWriter,
            out ResourceWriteMutation? mutation
        )
        {
            if (updateSource is not null)
            {
                resourceId = default;
                batchWriter = null;
                mutation = null;
                return false;
            }

            if (source is IStateWriteBatchParticipant<TSource> participant)
            {
                return participant.TryCreateBatchWrite(
                    new StateWriteRequest<TSource>(
                        toSource!(request.Value),
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

        public async ValueTask<StateWriteBatchPlan?> TryCreateBatchWriteAsync(
            StateWriteRequest<TTarget> request,
            CancellationToken cancellationToken = default
        )
        {
            if (!CanPrepareBatchWrite)
            {
                return null;
            }

            var mapped = updateSource is null
                ? toSource!(request.Value)
                : await UpdateSourceAsync(request, cancellationToken).ConfigureAwait(false);
            if (mapped is null)
            {
                throw new InvalidOperationException("The source projection returned a null value.");
            }
            var sourceRequest = new StateWriteRequest<TSource>(
                mapped,
                request.ExpectedRevision,
                request.CheckRevision
            );
            if (source is IAsyncStateWriteBatchParticipant<TSource> asyncParticipant)
            {
                return await asyncParticipant
                    .TryCreateBatchWriteAsync(sourceRequest, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (
                source is IStateWriteBatchParticipant<TSource> participant
                && participant.TryCreateBatchWrite(
                    sourceRequest,
                    out var resourceId,
                    out var batchWriter,
                    out var mutation
                )
                && batchWriter is not null
                && mutation is not null
            )
            {
                return new StateWriteBatchPlan(resourceId, batchWriter, mutation);
            }

            return null;
        }

        private async ValueTask<TSource> UpdateSourceAsync(
            StateWriteRequest<TTarget> request,
            CancellationToken cancellationToken
        )
        {
            var current = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (
                request.CheckRevision
                && !string.Equals(
                    request.ExpectedRevision,
                    current.Revision,
                    StringComparison.Ordinal
                )
            )
            {
                throw new StateConflictException(
                    "The projected source changed before its reverse update could be prepared."
                );
            }

            if (current.Status == StateReadStatus.Unavailable)
            {
                throw new InvalidOperationException(
                    "Cannot safely reverse-project a write because the source is unavailable."
                );
            }

            var currentValue = current.Status switch
            {
                StateReadStatus.NotFound => default,
                StateReadStatus.Success => current.Value
                    ?? throw new InvalidOperationException(
                        "The projected source returned a null value while preparing a reverse update."
                    ),
                _ => throw new InvalidOperationException(
                    $"Cannot safely reverse-project a source with status '{current.Status}'."
                ),
            };
            if (
                current.Status == StateReadStatus.Success
                && migrationChain is not null
                && current.Schema is { } schema
            )
            {
                currentValue = await migrationChain!
                    .MigrateAsync(currentValue!, schema, cancellationToken)
                    .ConfigureAwait(false);
            }

            TTarget? previousProjected = default;
            if (current.Status == StateReadStatus.Success)
            {
                previousProjected = toTarget(currentValue!);
                if (previousProjected is null)
                {
                    throw new InvalidOperationException(
                        "The source projection returned a null current value."
                    );
                }
            }
            var updated = updateSource!(previousProjected, request.Value, currentValue);
            if (updated is null)
            {
                throw new InvalidOperationException(
                    "The current-aware source update returned a null value."
                );
            }

            return updated;
        }
    }
}
