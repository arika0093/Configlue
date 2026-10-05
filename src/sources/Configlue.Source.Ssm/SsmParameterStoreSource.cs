using System.Security.Cryptography;
using System.Text;
using Amazon.SimpleSystemsManagement;
using Configlue.CompilerServices;
using Configlue.Internal;
using Configlue.Sources;

namespace Configlue.Source.Ssm;

/// <summary>
/// Reads a Parameter Store hierarchy into a generated Configlue fragment, with
/// opt-in writes and polling change observation.
/// </summary>
/// <remarks>
/// <para>Reads use <c>GetParametersByPath</c> with pagination and optional recursion.
/// <c>SecureString</c> decryption is an explicit opt-in so callers can apply
/// least-privilege IAM/KMS policies.</para>
/// <para>Request resilience is owned by the caller-supplied AWS SDK client: Configlue
/// issues one SDK call per page or write and maps a throttling outcome reported by
/// the client into an unavailable/error result. Configure retries on the SDK client.</para>
/// <para>Parameter Store has no watch stream. Change observation is optional
/// metadata polling with a configurable minimum interval and
/// cancellation/disposal support. Polling fetches metadata without decryption
/// and therefore avoids <c>SecureString</c> payload retrieval when versions
/// show nothing changed.</para>
/// <para>Writes are opt-in through <c>PutParameter</c>. Parameter versions are
/// not compare-and-swap preconditions: revision-match conditions are rejected
/// rather than given a false atomicity promise. <c>MustNotExist</c> maps to an
/// atomic non-overwriting create; unchecked writes follow
/// <see cref="SsmParameterStoreOptions.AllowOverwrite"/>.</para>
/// <para>Diagnostics never include <c>SecureString</c> values, decrypted payloads,
/// credentials, or KMS key material. Only names, versions, types, tiers, and
/// ARNs are preserved for provenance.</para>
/// </remarks>
public sealed class SsmParameterStoreSource<TFragment>
    : ISourceCapabilities<TFragment>,
        ISourceWriter<TFragment>,
        ISourceWatcher,
        IResourceIdentity,
        IDisposable
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private readonly ISsmParameterClient _client;
    private readonly Func<ConfiglueResourceContext, ISsmParameterClient>? _clientSelector;
    private readonly ConfiglueModelSchema _schema;
    private readonly string _rootPath;
    private readonly SsmParameterStoreOptions _options;
    private readonly bool _writable;
    private readonly bool _watchEnabled;
    private readonly Func<string, Type, object?> _valueParser;
    private readonly Dictionary<ConfiglueModelSchema, SsmMemberLookup> _lookups;
    private readonly WatchShutdown _watchShutdown = new();
    private readonly object _provenanceGate = new();
    private List<SsmParameterProvenance> _lastProvenance = [];
    private int _disposed;

    /// <summary>Creates a source for one Parameter Store hierarchy.</summary>
    public SsmParameterStoreSource(
        IAmazonSimpleSystemsManagement client,
        string rootPath,
        ConfiglueModelSchema schema,
        SsmParameterStoreOptions? options = null,
        bool writable = false,
        bool? watchChanges = null
    )
        : this(
            new SsmAwsParameterClient(client),
            rootPath,
            schema,
            options,
            clientSelector: options?.ClientSelector is { } selector
                ? context => new SsmAwsParameterClient(selector(context))
                : null,
            writable,
            watchChanges
        ) { }

    internal SsmParameterStoreSource(
        ISsmParameterClient client,
        string rootPath,
        ConfiglueModelSchema schema,
        SsmParameterStoreOptions? options = null,
        Func<ConfiglueResourceContext, ISsmParameterClient>? clientSelector = null,
        bool writable = false,
        bool? watchChanges = null
    )
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(schema);
        _options = options ?? new SsmParameterStoreOptions();
        _options.Validate();
        _rootPath = SsmParameterPath.NormalizeRootPath(rootPath);
        _client = client;
        _clientSelector = clientSelector;
        _schema = schema;
        _writable = writable;
        _watchEnabled = watchChanges ?? _options.WatchChanges;
        _valueParser = _options.ValueParser ?? SsmValueConverter.ParseDefault;
        _lookups = [];
        CollectLookups(schema, new HashSet<Type>());
    }

    /// <summary>The normalized root parameter path (starts and ends with <c>/</c>).</summary>
    public string RootPath => _rootPath;

    /// <summary>Safe, value-free provenance from the last successful read or poll.</summary>
    public IReadOnlyList<SsmParameterProvenance> LastProvenance
    {
        get
        {
            lock (_provenanceGate)
            {
                return _lastProvenance.ToArray();
            }
        }
    }

    /// <inheritdoc />
    public ISourceWriter<TFragment>? Writer => _writable ? this : null;

    /// <inheritdoc />
    public ISourceWatcher? Watcher => _watchEnabled ? this : null;

    /// <inheritdoc />
    public ResourceId GetResourceId(ConfiglueResourceContext context) =>
        _options.FixedResourceId ?? CreateResourceId(_rootPath);

    /// <inheritdoc />
    public override string ToString() => $"ssm:{_rootPath}";

    /// <summary>Redacted diagnostics: root path and cached parameter count only.</summary>
    public string Describe()
    {
        var count = LastProvenance.Count;
        return $"ssm:{_rootPath} parameters={count}";
    }

    /// <inheritdoc />
    public async ValueTask<StateReadResult<TFragment>> ReadAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var parameters = await FetchAllAsync(
                GetClient(context),
                _options.WithDecryption,
                cancellationToken
            )
            .ConfigureAwait(false);
        RememberProvenance(parameters);
        var revision = SsmParameterPath.CreateRevision(parameters);
        var mapped = MapParameters(parameters, out var invalidFailure);
        if (invalidFailure is not null)
        {
            return StateReadResult<TFragment>.InvalidPayload(default, revision) with
            {
                Schema = _schema.ToMetadata(),
            };
        }

        if (!mapped.Matched)
        {
            return StateReadResult<TFragment>.NotFound(revision);
        }

        return StateReadResult<TFragment>.Success(
            (TFragment)mapped.Fragment,
            revision,
            _schema.ToMetadata()
        );
    }

    /// <inheritdoc />
    public async ValueTask<StateWriteResult> WriteAsync(
        ConfiglueResourceContext context,
        StateWriteRequest<TFragment> request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_writable)
        {
            throw new InvalidOperationException(
                $"The Parameter Store source '{_rootPath}' is read-only."
            );
        }

        if (request.Condition.IsMatch)
        {
            // Parameter versions are not atomic compare-and-swap preconditions.
            // Refuse revision-match writes rather than promise stale-write safety.
            throw new InvalidOperationException(
                $"The Parameter Store source '{_rootPath}' does not support revision-conditioned writes. Parameter versions are not atomic compare-and-swap preconditions."
            );
        }

        var client = GetClient(context);
        var leaves = FlattenFragment(request.Value);
        if (leaves.Count == 0)
        {
            var emptyRevision = SsmParameterPath.CreateRevision(
                await FetchMetadataAsync(client, cancellationToken).ConfigureAwait(false)
            );
            return new StateWriteResult(emptyRevision);
        }

        var overwrite = _options.AllowOverwrite && !request.Condition.IsMustNotExist;
        foreach (var leaf in leaves)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullName = _rootPath + string.Join("/", leaf.Path);
            var memberPath = string.Join(".", leaf.Path);
            var writeType =
                _options.WriteTypeSelector?.Invoke(memberPath) ?? _options.WriteParameterType;
            var keyId = _options.WriteKeyIdSelector?.Invoke(memberPath) ?? _options.WriteKeyId;
            var valueText =
                writeType == SsmParameterType.StringList && leaf.Value is not string
                    ? SsmValueConverter.FormatStringList(leaf.Value)
                    : SsmValueConverter.Format(leaf.Value, _options.JsonSerializerOptions);
            var tierText = _options.WriteTier switch
            {
                SsmParameterTier.Standard => "Standard",
                SsmParameterTier.Advanced => "Advanced",
                SsmParameterTier.IntelligentTiering => "Intelligent-Tiering",
                _ => null,
            };
            try
            {
                await client
                    .PutParameterAsync(
                        fullName,
                        valueText,
                        ToAwsType(writeType),
                        keyId,
                        overwrite,
                        tierText,
                        _options.WriteDataType,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (IsAlreadyExists(exception))
            {
                throw new StateConflictException(
                    $"The Parameter Store parameter '{fullName}' already exists."
                );
            }
        }

        var metadata = await FetchMetadataAsync(client, cancellationToken).ConfigureAwait(false);
        RememberProvenance(metadata);
        return new StateWriteResult(SsmParameterPath.CreateRevision(metadata));
    }

    /// <inheritdoc />
    public async ValueTask WaitForChangeAsync(
        ConfiglueResourceContext context,
        string? observedRevision,
        CancellationToken cancellationToken = default
    )
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_watchEnabled)
        {
            throw new InvalidOperationException(
                $"The Parameter Store source '{_rootPath}' does not support watching."
            );
        }

        await _watchShutdown
            .WaitAsync(
                watchCancellationToken =>
                    PollingWatch.WaitForRevisionChangeAsync(
                        cancellation => ReadMetadataRevisionAsync(context, cancellation),
                        observedRevision,
                        _options.PollInterval,
                        watchCancellationToken
                    ),
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _watchShutdown.Signal();
    }

    /// <summary>
    /// Metadata-only revision read: versions determine change without decrypting
    /// <c>SecureString</c> payloads.
    /// </summary>
    private async ValueTask<string?> ReadMetadataRevisionAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken
    )
    {
        var metadata = await FetchMetadataAsync(GetClient(context), cancellationToken)
            .ConfigureAwait(false);
        RememberProvenance(metadata);
        return SsmParameterPath.CreateRevision(metadata);
    }

    private ISsmParameterClient GetClient(ConfiglueResourceContext context) =>
        _clientSelector?.Invoke(context) ?? _client;

    private async Task<List<SsmParameterData>> FetchAllAsync(
        ISsmParameterClient client,
        bool withDecryption,
        CancellationToken cancellationToken
    )
    {
        var all = new List<SsmParameterData>();
        string? nextToken = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await GetPageAsync(client, withDecryption, nextToken, cancellationToken)
                .ConfigureAwait(false);
            all.AddRange(page.Parameters);
            nextToken = page.NextToken;
        } while (nextToken is not null);

        return all;
    }

    private Task<List<SsmParameterData>> FetchMetadataAsync(
        ISsmParameterClient client,
        CancellationToken cancellationToken
    ) => FetchAllAsync(client, withDecryption: false, cancellationToken);

    private async Task<SsmParameterPage> GetPageAsync(
        ISsmParameterClient client,
        bool withDecryption,
        string? nextToken,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await client
                .GetParametersByPathAsync(
                    _rootPath,
                    _options.Recursive,
                    withDecryption,
                    nextToken,
                    _options.PageSize,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (SsmThrottling.IsMissing(exception))
        {
            return new SsmParameterPage([], NextToken: null);
        }
        catch (Exception exception) when (SsmThrottling.IsThrottling(exception))
        {
            throw new InvalidOperationException(
                $"The Parameter Store hierarchy '{_rootPath}' is temporarily unavailable.",
                exception
            );
        }
    }

    private void RememberProvenance(List<SsmParameterData> parameters)
    {
        var provenance = new List<SsmParameterProvenance>(parameters.Count);
        foreach (var parameter in parameters)
        {
            provenance.Add(
                new SsmParameterProvenance
                {
                    Name = parameter.Name,
                    Version = parameter.Version,
                    Type = parameter.Type,
                    Tier = parameter.Tier,
                    Arn = parameter.Arn,
                    DataType = parameter.DataType,
                }
            );
        }

        lock (_provenanceGate)
        {
            _lastProvenance = provenance;
        }
    }

    private (IConfiglueFragment Fragment, bool Matched) MapParameters(
        List<SsmParameterData> parameters,
        out string? invalidFailure
    )
    {
        invalidFailure = null;
        IConfiglueFragment fragment = _schema.CreateEmptyFragment();
        var matchedAny = false;
        var ordered = parameters.OrderBy(static p => p.Name, StringComparer.Ordinal).ToArray();
        foreach (var parameter in ordered)
        {
            if (
                !SsmParameterPath.TryGetRelative(_rootPath, parameter.Name, out var relative, out _)
            )
            {
                continue;
            }

            var segments = SsmParameterPath.SplitRelative(relative);
            if (segments.Any(string.IsNullOrWhiteSpace))
            {
                invalidFailure = $"Parameter '{parameter.Name}' contains an empty path segment.";
                return (fragment, false);
            }

            var resolved = ResolveCanonicalPath(_schema, segments, parameter.Name);
            if (resolved is null)
            {
                // Unknown leaf paths are ignored so unrelated parameters can
                // share the same root hierarchy.
                continue;
            }

            if (resolved.IsNestedTarget)
            {
                invalidFailure =
                    $"Parameter '{parameter.Name}' names a nested model. Use deeper path segments to set its members.";
                return (fragment, false);
            }

            var member = resolved.Member;
            if (member.NestedSchemaFactory is not null)
            {
                invalidFailure =
                    $"Parameter '{parameter.Name}' names a nested model. Use deeper path segments to set its members.";
                return (fragment, false);
            }

            if (
                string.Equals(parameter.Type, "SecureString", StringComparison.OrdinalIgnoreCase)
                && !_options.WithDecryption
            )
            {
                invalidFailure =
                    $"Parameter '{parameter.Name}' is a SecureString but decryption is not enabled.";
                return (fragment, false);
            }

            var isStringList = string.Equals(
                parameter.Type,
                "StringList",
                StringComparison.OrdinalIgnoreCase
            );
            object? parsed;
            try
            {
                parsed = SsmValueConverter.Parse(
                    parameter.Value ?? string.Empty,
                    isStringList,
                    member.ValueType,
                    _valueParser,
                    _options.JsonSerializerOptions
                );
            }
            catch (FormatException exception)
            {
                invalidFailure =
                    $"Parameter '{parameter.Name}' is not a valid value for member '{member.Name}': {exception.Message}";
                return (fragment, false);
            }

            fragment = SetLeafValue(fragment, _schema, resolved.CanonicalPath, 0, parsed);
            matchedAny = true;
        }

        return (fragment, matchedAny);
    }

    private ResolvedPath? ResolveCanonicalPath(
        ConfiglueModelSchema schema,
        string[] segments,
        string parameterName
    )
    {
        var canonical = new string[segments.Length];
        var current = schema;
        for (var index = 0; index < segments.Length; index++)
        {
            if (!_lookups[current].TryResolve(segments[index], out var member, out var ambiguous))
            {
                if (ambiguous)
                {
                    throw new FormatException(
                        $"Parameter '{parameterName}' segment '{segments[index]}' is ambiguous in schema '{current.Id}'."
                    );
                }

                return null;
            }

            canonical[index] = member.Name;
            if (index == segments.Length - 1)
            {
                return new ResolvedPath(canonical, member, member.NestedSchemaFactory is not null);
            }

            if (member.NestedSchemaFactory is null)
            {
                throw new FormatException(
                    $"Parameter '{parameterName}' continues past non-nested member '{member.Name}'."
                );
            }

            current = member.NestedSchemaFactory();
        }

        return null;
    }

    private static IConfiglueFragment SetLeafValue(
        IConfiglueFragment fragment,
        ConfiglueModelSchema schema,
        string[] path,
        int pathIndex,
        object? value
    )
    {
        var member = FindMember(schema, path[pathIndex]);
        if (pathIndex == path.Length - 1)
        {
            return fragment.WithMember(member.Id, value);
        }

        var nestedSchema = member.NestedSchemaFactory!();
        var nested =
            FindPresentMember(fragment, member.Id) as IConfiglueFragment
            ?? nestedSchema.CreateEmptyFragment();
        var updated = SetLeafValue(nested, nestedSchema, path, pathIndex + 1, value);
        return fragment.WithMember(member.Id, updated);
    }

    private static ConfiglueMemberSchema FindMember(ConfiglueModelSchema schema, string name)
    {
        var match = schema.Members.FirstOrDefault(member =>
            string.Equals(member.Name, name, StringComparison.Ordinal)
        );
        if (!match.IsDefault)
        {
            return match;
        }

        throw new InvalidOperationException($"Schema '{schema.Id}' has no member '{name}'.");
    }

    private static object? FindPresentMember(IConfiglueFragment fragment, int memberId)
    {
        foreach (var member in fragment.EnumeratePresentMembersFast())
        {
            if (member.Id == memberId)
            {
                return member.Value;
            }
        }

        return null;
    }

    private List<LeafAssignment> FlattenFragment(TFragment fragment)
    {
        var leaves = new List<LeafAssignment>();
        CollectLeaves(fragment, _schema, [], leaves);
        return leaves;
    }

    private void CollectLeaves(
        IConfiglueFragment fragment,
        ConfiglueModelSchema schema,
        List<string> parentPath,
        List<LeafAssignment> leaves
    )
    {
        foreach (var present in fragment.EnumeratePresentMembersFast())
        {
            if (!schema.TryGetMember(present.Id, out var member))
            {
                continue;
            }

            var path = new List<string>(parentPath) { member.Name };
            if (member.NestedSchemaFactory is not null)
            {
                if (present.Value is null)
                {
                    throw new InvalidOperationException(
                        $"Cannot write present-null nested member '{string.Join(".", path)}' to Parameter Store."
                    );
                }

                if (present.Value is IConfiglueFragment nested)
                {
                    CollectLeaves(nested, member.NestedSchemaFactory(), path, leaves);
                    continue;
                }

                throw new InvalidOperationException(
                    $"Cannot write nested member '{string.Join(".", path)}' with an unexpected value."
                );
            }

            leaves.Add(new LeafAssignment([.. path], present.Value));
        }
    }

    private void CollectLookups(ConfiglueModelSchema schema, HashSet<Type> ancestors)
    {
        if (!ancestors.Add(schema.ModelType))
        {
            return;
        }

        _lookups[schema] = SsmMemberLookup.Create(schema);
        foreach (
            var member in schema.Members.Where(static item => item.NestedSchemaFactory is not null)
        )
        {
            CollectLookups(member.NestedSchemaFactory!(), ancestors);
        }

        ancestors.Remove(schema.ModelType);
    }

    private static string ToAwsType(SsmParameterType type) =>
        type switch
        {
            SsmParameterType.StringList => "StringList",
            SsmParameterType.SecureString => "SecureString",
            _ => "String",
        };

    private static bool IsAlreadyExists(Exception exception) =>
        exception is Amazon.SimpleSystemsManagement.Model.ParameterAlreadyExistsException
        || (
            exception is Amazon.Runtime.AmazonServiceException service
            && string.Equals(service.ErrorCode, "ParameterAlreadyExists", StringComparison.Ordinal)
        );

    private static ResourceId CreateResourceId(string rootPath)
    {
        var identity = Encoding.UTF8.GetBytes("ssm\n" + rootPath);
        return new ResourceId(
            $"ssm:{Convert.ToHexString(SHA256.HashData(identity)).ToLowerInvariant()}"
        );
    }

    private sealed record ResolvedPath(
        string[] CanonicalPath,
        ConfiglueMemberSchema Member,
        bool IsNestedTarget
    );

    private sealed record LeafAssignment(string[] Path, object? Value);
}
