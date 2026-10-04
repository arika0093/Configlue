using System.Collections.Concurrent;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using Configlue.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Configlue;

/// <summary>
/// Owns read-side and write-side model validation for one runtime.
///
/// Holds the configured validators, the data-annotations opt-in, and the state
/// name used in failure messages. All member/data-annotation metadata caches live
/// here; resolution and write code calls in with explicit fragments so no shared
/// mutable resolution state is needed.
/// </summary>
internal sealed class RuntimeValidationPipeline<TModel, TFragment>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private readonly IConfiglueValidator<TModel>[] _validators;
    private readonly string _stateName;
    private readonly bool _validateDataAnnotations;
    private readonly RuntimeDiagnosticRecorder _diagnostics;

    internal RuntimeValidationPipeline(
        IConfiglueValidator<TModel>[] validators,
        bool validateDataAnnotations,
        string stateName,
        RuntimeDiagnosticRecorder diagnostics
    )
    {
        _validators = validators;
        _validateDataAnnotations = validateDataAnnotations;
        _stateName = stateName;
        _diagnostics = diagnostics;
    }

    internal void Validate(TModel value)
    {
        if (
            _validators.Length == 0
            && (
                !_validateDataAnnotations
                || !ConfiglueRuntimeCapabilities.IsDynamicCodeSupported
                || !HasValidationMetadata(value.GetType())
            )
        )
        {
            return;
        }

        var failures = new List<string>();
        CollectValidationFailures(value, failures);
        if (failures.Count > 0)
        {
            SanitizeFailures(failures, value);
            _diagnostics.Record(
                ConfiglueDiagnosticEventKind.ValidationFailed,
                errorCategory: typeof(ConfiglueValidationException).FullName
            );
            throw new ConfiglueValidationException(_stateName, typeof(TModel), failures);
        }
    }

    private void CollectValidationFailures(TModel value, List<string> failures)
    {
        foreach (var validator in _validators)
        {
            failures.AddRange(validator.Validate(_stateName, value));
        }

        if (
            _validateDataAnnotations
            && ConfiglueRuntimeCapabilities.IsDynamicCodeSupported
            && HasValidationMetadata(value.GetType())
        )
        {
            var validationResults = new List<ValidationResult>();
            Validator.TryValidateObject(
                value,
                new ValidationContext(value),
                validationResults,
                validateAllProperties: true
            );
            failures.AddRange(
                validationResults.Select(result =>
                    result.ErrorMessage ?? "Configuration validation failed."
                )
            );
        }
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "This reflection path runs only when dynamic code is supported."
    )]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2067",
        Justification = "This reflection path runs only when dynamic code is supported."
    )]
    private static bool HasValidationMetadata(Type modelType) =>
        RuntimeValidationCaches.ModelValidationMetadata.GetOrAdd(
            modelType,
            static type =>
                typeof(IValidatableObject).IsAssignableFrom(type)
                || TypeDescriptor.GetAttributes(type).OfType<ValidationAttribute>().Any()
                || TypeDescriptor
                    .GetProperties(type)
                    .Cast<PropertyDescriptor>()
                    .Any(static property => property.Attributes.OfType<ValidationAttribute>().Any())
        );

    internal void ValidateContribution(
        StateSource<TFragment> source,
        TFragment fragment,
        TFragment defaultsFragment
    )
    {
        if (_validators.Length == 0 && !_validateDataAnnotations)
        {
            return;
        }

        var failures = new List<string>();
        var contributionModel = RuntimeModel<TModel, TFragment>.FromFragment(
            defaultsFragment.Merge(fragment)
        );
        if (_validateDataAnnotations)
        {
            CollectMemberFailures(
                fragment.Schema,
                fragment,
                contributionModel,
                string.Empty,
                failures
            );
        }

        CollectValidationFailures(contributionModel, failures);
        if (failures.Count > 1)
        {
            failures = failures.Distinct(StringComparer.Ordinal).ToList();
        }

        if (failures.Count == 0)
        {
            return;
        }

        SanitizeFailures(failures, contributionModel);
        throw CreateContributionValidationException(source, failures);
    }

    // Isolate the capturing failure formatter so successful validation never allocates its closure.
    private ConfiglueValidationException CreateContributionValidationException(
        StateSource<TFragment> source,
        List<string> failures
    ) =>
        new(
            _stateName,
            typeof(TModel),
            failures.Select(failure => $"Source '{source.Id}': {failure}")
        );

    internal IConfiglueFragment PruneInvalidMembers(
        StateSource<TFragment> source,
        IConfiglueFragment fragment,
        TFragment defaultsFragment
    )
    {
        if (
            !_validateDataAnnotations
            || !ConfiglueRuntimeCapabilities.IsDynamicCodeSupported
            || !HasMemberValidationMetadata(fragment.Schema)
        )
        {
            return fragment;
        }

        var failures = new List<string>();
        object? container = null;
        if (
            _validateDataAnnotations
            && ConfiglueRuntimeCapabilities.IsDynamicCodeSupported
            && fragment is TFragment typedFragment
        )
        {
            // Snapshot the container once so members pruned earlier in this pass do not
            // change the meaning of context-dependent attributes evaluated later.
            container = RuntimeModel<TModel, TFragment>.FromFragment(
                defaultsFragment.Merge(typedFragment)
            );
        }

        fragment = PruneInvalidMembers(
            fragment.Schema,
            fragment,
            container,
            string.Empty,
            failures
        );
        if (failures.Count == 0)
        {
            return fragment;
        }

        _diagnostics.Record(
            ConfiglueDiagnosticEventKind.ValidationFailed,
            sourceId: source.Id,
            errorCategory: typeof(ConfiglueValidationException).FullName
        );
        return fragment;
    }

    private IConfiglueFragment PruneInvalidMembers(
        ConfiglueModelSchema schema,
        IConfiglueFragment fragment,
        object? container,
        string prefix,
        List<string> failures
    )
    {
        if (!_validateDataAnnotations || !ConfiglueRuntimeCapabilities.IsDynamicCodeSupported)
        {
            return fragment;
        }

        foreach (var present in fragment.EnumeratePresentMembersFast())
        {
            if (!RuntimeState.TryGetMember(schema, present.Id, out var found))
            {
                continue;
            }

            var path = prefix + found.Name;
            if (found.NestedSchemaFactory is not null && present.Value is IConfiglueFragment nested)
            {
                var priorFailureCount = failures.Count;
                var nestedContainer = container is not null
                    ? TryGetNestedContainer(container, found)
                    : null;
                if (nestedContainer is null)
                {
                    continue;
                }

                var prunedNested = PruneInvalidMembers(
                    found.NestedSchemaFactory(),
                    nested,
                    nestedContainer,
                    path + ".",
                    failures
                );
                if (failures.Count != priorFailureCount)
                {
                    fragment = fragment.WithMember(found.Id, prunedNested);
                }

                continue;
            }

            if (container is null)
            {
                continue;
            }

            if (
                CollectMemberAttributeFailures(
                    GetMemberValidationAttributes(schema, found.Id),
                    container,
                    found.Name,
                    present.Value,
                    out var message
                )
            )
            {
                failures.Add($"{path}: {message}");
                fragment = fragment.WithoutMember(found.Id);
            }
        }

        return fragment;
    }

    internal void ValidateResolvedModel(TModel model, IConfiglueFragment merged)
    {
        var validateDataAnnotations =
            _validateDataAnnotations && ConfiglueRuntimeCapabilities.IsDynamicCodeSupported;
        var hasMemberValidation =
            validateDataAnnotations && HasMemberValidationMetadata(merged.Schema);
        var hasModelValidation = validateDataAnnotations && HasValidationMetadata(model.GetType());
        if (_validators.Length == 0 && !hasMemberValidation && !hasModelValidation)
        {
            return;
        }

        var failures = new List<string>();
        if (hasMemberValidation)
        {
            CollectMemberFailures(merged.Schema, merged, model, string.Empty, failures);
        }

        CollectValidationFailures(model, failures);
        if (failures.Count == 0)
        {
            return;
        }

        if (failures.Count > 1)
        {
            failures = failures.Distinct(StringComparer.Ordinal).ToList();
        }

        SanitizeFailures(failures, model);
        throw new ConfiglueValidationException(_stateName, typeof(TModel), failures);
    }

    private static bool HasMemberValidationMetadata(ConfiglueModelSchema schema) =>
        RuntimeValidationCaches.MemberValidationMetadata.TryGetValue(
            schema.ModelType,
            out var cached
        )
            ? cached
            : RuntimeValidationCaches.MemberValidationMetadata.GetOrAdd(
                schema.ModelType,
                HasMemberValidationMetadata(schema, [])
            );

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "Callers guard member metadata inspection on dynamic code support."
    )]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "Callers guard member metadata inspection on dynamic code support."
    )]
    private static bool HasMemberValidationMetadata(
        ConfiglueModelSchema schema,
        HashSet<Type> visited
    )
    {
        if (!visited.Add(schema.ModelType))
        {
            return false;
        }

        foreach (var member in schema.Members)
        {
            if (GetMemberValidationAttributes(schema, member.Id).Length > 0)
            {
                return true;
            }

            if (
                member.NestedSchemaFactory?.Invoke() is { } nestedSchema
                && HasMemberValidationMetadata(nestedSchema, visited)
            )
            {
                return true;
            }
        }

        return false;
    }

    private static void CollectMemberFailures(
        ConfiglueModelSchema schema,
        IConfiglueFragment fragment,
        object container,
        string prefix,
        List<string> failures,
        List<int>? invalidMemberIds = null
    )
    {
        if (!ConfiglueRuntimeCapabilities.IsDynamicCodeSupported)
        {
            return;
        }

        foreach (var present in fragment.EnumeratePresentMembersFast())
        {
            if (!RuntimeState.TryGetMember(schema, present.Id, out var found))
            {
                continue;
            }

            var path = prefix + found.Name;
            if (found.NestedSchemaFactory is not null && present.Value is IConfiglueFragment nested)
            {
                var nestedContainer = TryGetNestedContainer(container, found);
                if (nestedContainer is null)
                {
                    continue;
                }

                var nestedCount = failures.Count;
                CollectMemberFailures(
                    found.NestedSchemaFactory(),
                    nested,
                    nestedContainer,
                    path + ".",
                    failures,
                    invalidMemberIds: null
                );
                if (failures.Count != nestedCount)
                {
                    invalidMemberIds?.Add(found.Id);
                }

                continue;
            }

            if (
                CollectMemberAttributeFailures(
                    GetMemberValidationAttributes(schema, found.Id),
                    container,
                    found.Name,
                    present.Value,
                    out var message
                )
            )
            {
                failures.Add(
                    $"{path}: {ConfiglueSecrets.RedactMessage(message, present.Value, found.IsSecret)}"
                );
                invalidMemberIds?.Add(found.Id);
            }
        }
    }

    private static bool CollectMemberAttributeFailures(
        ValidationAttribute[] attributes,
        object containerModel,
        string memberName,
        object? value,
        out string message
    )
    {
        for (var index = 0; index < attributes.Length; index++)
        {
            var attribute = attributes[index];
            var context = new ValidationContext(containerModel) { MemberName = memberName };
            var result = attribute.GetValidationResult(value, context);
            if (result != ValidationResult.Success)
            {
                message = result?.ErrorMessage ?? attribute.FormatErrorMessage(memberName);
                return true;
            }
        }

        message = string.Empty;
        return false;
    }

    private static void SanitizeFailures(List<string> failures, TModel value)
    {
        var secrets = new List<string>();
        CollectSecretPlaintexts(RuntimeModel<TModel, TFragment>.Schema, value, secrets);
        if (secrets.Count == 0)
        {
            return;
        }

        for (var index = 0; index < failures.Count; index++)
        {
            var sanitized = failures[index];
            for (var secretIndex = 0; secretIndex < secrets.Count; secretIndex++)
            {
                var secret = secrets[secretIndex];
                if (sanitized.IndexOf(secret, StringComparison.Ordinal) >= 0)
                {
                    sanitized = sanitized.Replace(secret, ConfiglueSecrets.RedactedText);
                }
            }

            failures[index] = sanitized;
        }
    }

    private static void CollectSecretPlaintexts(
        ConfiglueModelSchema schema,
        object? container,
        List<string> secrets
    )
    {
        if (container is null)
        {
            return;
        }

        foreach (var member in schema.Members)
        {
            object? memberValue;
            try
            {
                memberValue = member.GetValue?.Invoke(container);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException)
            {
                continue;
            }

            if (memberValue is null)
            {
                continue;
            }

            if (member.IsSecret)
            {
                switch (memberValue)
                {
                    case string text when text.Length > 0:
                        secrets.Add(text);
                        break;
                    case System.Collections.IEnumerable sequence when memberValue is not string:
                        foreach (var element in sequence)
                        {
                            if (element?.ToString() is { Length: > 0 } elementText)
                            {
                                secrets.Add(elementText);
                            }
                        }

                        break;
                    default:
                        if (memberValue.ToString() is { Length: > 0 } scalar)
                        {
                            secrets.Add(scalar);
                        }

                        break;
                }
            }

            if (
                member.NestedSchemaFactory?.Invoke() is { } nestedSchema
                && memberValue is not string
                && memberValue is not System.Collections.IEnumerable
            )
            {
                CollectSecretPlaintexts(nestedSchema, memberValue, secrets);
            }
        }
    }

    [RequiresUnreferencedCode(
        "Member validation reflects over model properties that trimming may remove."
    )]
    [RequiresDynamicCode("Member validation inspects model properties at runtime.")]
    private static ValidationAttribute[] GetMemberValidationAttributes(
        Type modelType,
        string memberName
    ) =>
        RuntimeValidationCaches.MemberValidationAttributes.GetOrAdd(
            (modelType, memberName),
            static key =>
                key.ModelType.GetProperty(
                        key.MemberName,
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase
                    )
                    ?.GetCustomAttributes(typeof(ValidationAttribute), inherit: true)
                    .OfType<ValidationAttribute>()
                    .ToArray()
                ?? []
        );

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "Callers guard attribute inspection on dynamic code support."
    )]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "Callers guard attribute inspection on dynamic code support."
    )]
    private static ValidationAttribute[] GetMemberValidationAttributes(
        ConfiglueModelSchema schema,
        int memberId
    )
    {
        var attributes = ConfiglueMemberValidationAttributeCache.GetOrAdd(
            schema.ModelType,
            schema,
            static candidate => BuildMemberValidationAttributes(candidate)
        );
        return attributes.TryGetValue(memberId, out var found) ? found : [];
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "Callers guard attribute inspection on dynamic code support."
    )]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "Callers guard attribute inspection on dynamic code support."
    )]
    private static IReadOnlyDictionary<int, ValidationAttribute[]> BuildMemberValidationAttributes(
        ConfiglueModelSchema schema
    )
    {
        var attributes = new Dictionary<int, ValidationAttribute[]>();
        foreach (var member in schema.Members)
        {
            var memberAttributes = GetMemberValidationAttributes(schema.ModelType, member.Name);
            if (memberAttributes.Length > 0)
            {
                attributes[member.Id] = memberAttributes;
            }
        }

        return attributes;
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "Callers guard member metadata inspection on dynamic code support."
    )]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "Callers guard member metadata inspection on dynamic code support."
    )]
    private static object? TryGetNestedContainer(object container, ConfiglueMemberSchema member)
    {
        try
        {
            if (ConfiglueModelSchemaCatalog.TryGet(container.GetType(), out var modelSchema))
            {
                var candidates = modelSchema.Members;
                for (var candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
                {
                    var candidate = candidates[candidateIndex];
                    if (
                        string.Equals(candidate.Name, member.Name, StringComparison.Ordinal)
                        && candidate.GetValue is not null
                    )
                    {
                        return candidate.GetValue(container);
                    }
                }
            }

            return container
                .GetType()
                .GetProperty(
                    member.Name,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase
                )
                ?.GetValue(container);
        }
        catch (Exception ex) when (ex is TargetInvocationException || ex is ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>
/// Validation metadata caches shared across all closed model types.
///
/// The dictionaries are keyed by runtime <see cref="Type"/>, so sharing one
/// instance across constructed types is intentional.
/// </summary>
internal static class RuntimeValidationCaches
{
    internal static readonly ConcurrentDictionary<
        (Type ModelType, string MemberName),
        ValidationAttribute[]
    > MemberValidationAttributes = new();
    internal static readonly ConcurrentDictionary<Type, bool> ModelValidationMetadata = new();
    internal static readonly ConcurrentDictionary<Type, bool> MemberValidationMetadata = new();
}

internal static class ConfiglueMemberValidationAttributeCache
{
    private static readonly ConcurrentDictionary<
        Type,
        IReadOnlyDictionary<int, ValidationAttribute[]>
    > ById = new();

    public static IReadOnlyDictionary<int, ValidationAttribute[]> GetOrAdd(
        Type modelType,
        ConfiglueModelSchema schema,
        Func<ConfiglueModelSchema, IReadOnlyDictionary<int, ValidationAttribute[]>> factory
    ) =>
        ById.TryGetValue(modelType, out var cached)
            ? cached
            : ById.GetOrAdd(modelType, factory(schema));
}

internal static class ConfiglueRuntimeCapabilities
{
#if NETSTANDARD
    private static readonly PropertyInfo? IsDynamicCodeSupportedProperty = Type.GetType(
            "System.Runtime.CompilerServices.RuntimeFeature, System.Runtime"
        )
        ?.GetProperty("IsDynamicCodeSupported", BindingFlags.Public | BindingFlags.Static);

    public static bool IsDynamicCodeSupported =>
        IsDynamicCodeSupportedProperty?.GetValue(null) as bool? ?? true;
#else
    public static bool IsDynamicCodeSupported => RuntimeFeature.IsDynamicCodeSupported;
#endif
}
