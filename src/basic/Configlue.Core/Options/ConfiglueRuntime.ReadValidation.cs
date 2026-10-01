using System.Collections.Concurrent;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using Configlue.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Configlue;

internal sealed partial class ConfiglueRuntime<TModel, TFragment>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private static bool TryGetMember(
        ConfiglueModelSchema schema,
        int memberId,
        out ConfiglueMemberSchema member
    )
    {
        var members = schema.Members;
        for (var index = 0; index < members.Count; index++)
        {
            if (members[index].Id == memberId)
            {
                member = members[index];
                return true;
            }
        }

        member = default;
        return false;
    }

    private static bool TryGetMemberByName(
        ConfiglueModelSchema schema,
        string name,
        out ConfiglueMemberSchema member
    )
    {
        var members = schema.Members;
        for (var index = 0; index < members.Count; index++)
        {
            if (string.Equals(members[index].Name, name, StringComparison.Ordinal))
            {
                member = members[index];
                return true;
            }
        }

        member = default;
        return false;
    }

    private void Validate(TModel value)
    {
        var failures = new List<string>();
        CollectValidationFailures(value, failures);
        if (failures.Count > 0)
        {
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
        ModelValidationMetadata.GetOrAdd(
            modelType,
            static type =>
                typeof(IValidatableObject).IsAssignableFrom(type)
                || TypeDescriptor.GetAttributes(type).OfType<ValidationAttribute>().Any()
                || TypeDescriptor
                    .GetProperties(type)
                    .Cast<PropertyDescriptor>()
                    .Any(static property => property.Attributes.OfType<ValidationAttribute>().Any())
        );

    private void ValidateContribution(StateSource<TFragment> source, TFragment fragment)
    {
        var failures = new List<string>();
        if (_validateDataAnnotations)
        {
            CollectMemberFailures(fragment.Schema, fragment, string.Empty, failures);
        }

        CollectValidationFailures(FromFragment(_modelDefaultsFragment.Merge(fragment)), failures);
        if (failures.Count > 1)
        {
            failures = failures.Distinct(StringComparer.Ordinal).ToList();
        }

        if (failures.Count == 0)
        {
            return;
        }

        _logger?.LogWarning(
            ReadValidationEvent,
            "Configuration source {SourceId} contributed invalid values for {ModelType} state {StateName}: {Failures}.",
            source.Id,
            typeof(TModel).FullName,
            _stateName,
            string.Join("; ", failures)
        );
        throw new ConfiglueValidationException(
            _stateName,
            typeof(TModel),
            failures.Select(failure => $"Source '{source.Id}': {failure}")
        );
    }

    private IConfiglueFragment PruneInvalidMembers(
        StateSource<TFragment> source,
        IConfiglueFragment fragment
    )
    {
        var failures = new List<string>();
        fragment = PruneInvalidMembers(fragment.Schema, fragment, string.Empty, failures);
        if (failures.Count == 0)
        {
            return fragment;
        }

        _logger?.LogWarning(
            ReadValidationEvent,
            "Ignoring invalid values from configuration source {SourceId} for {ModelType} state {StateName}: {Failures}.",
            source.Id,
            typeof(TModel).FullName,
            _stateName,
            string.Join("; ", failures)
        );
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
        string prefix,
        List<string> failures
    )
    {
        if (!_validateDataAnnotations || !ConfiglueRuntimeCapabilities.IsDynamicCodeSupported)
        {
            return fragment;
        }

        foreach (var present in fragment.EnumeratePresentMembers())
        {
            if (!TryGetMember(schema, present.Id, out var found))
            {
                continue;
            }

            var path = prefix + found.Name;
            if (found.NestedSchemaFactory is not null && present.Value is IConfiglueFragment nested)
            {
                var priorFailureCount = failures.Count;
                var prunedNested = PruneInvalidMembers(
                    found.NestedSchemaFactory(),
                    nested,
                    path + ".",
                    failures
                );
                if (failures.Count != priorFailureCount)
                {
                    fragment = fragment.WithMember(found.Id, prunedNested);
                }

                continue;
            }

            if (
                CollectMemberAttributeFailures(
                    GetMemberValidationAttributes(schema, found.Id),
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

    private void ValidateResolvedModel(TModel model, IConfiglueFragment merged)
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
            CollectMemberFailures(merged.Schema, merged, string.Empty, failures);
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

        _logger?.LogWarning(
            ReadValidationEvent,
            "Resolved configuration for {ModelType} state {StateName} failed validation: {Failures}.",
            typeof(TModel).FullName,
            _stateName,
            string.Join("; ", failures)
        );
        throw new ConfiglueValidationException(_stateName, typeof(TModel), failures);
    }

    private static bool HasMemberValidationMetadata(ConfiglueModelSchema schema) =>
        MemberValidationMetadata.GetOrAdd(
            schema.ModelType,
            _ => HasMemberValidationMetadata(schema, [])
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
        string prefix,
        List<string> failures,
        List<int>? invalidMemberIds = null
    )
    {
        if (!ConfiglueRuntimeCapabilities.IsDynamicCodeSupported)
        {
            return;
        }

        foreach (var present in fragment.EnumeratePresentMembers())
        {
            if (!TryGetMember(schema, present.Id, out var found))
            {
                continue;
            }

            var path = prefix + found.Name;
            if (found.NestedSchemaFactory is not null && present.Value is IConfiglueFragment nested)
            {
                var nestedCount = failures.Count;
                CollectMemberFailures(
                    found.NestedSchemaFactory(),
                    nested,
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
                    found.Name,
                    present.Value,
                    out var message
                )
            )
            {
                failures.Add($"{path}: {message}");
                invalidMemberIds?.Add(found.Id);
            }
        }
    }

    private static bool CollectMemberAttributeFailures(
        ValidationAttribute[] attributes,
        string memberName,
        object? value,
        out string message
    )
    {
        for (var index = 0; index < attributes.Length; index++)
        {
            if (!attributes[index].IsValid(value))
            {
                message = attributes[index].FormatErrorMessage(memberName);
                return true;
            }
        }

        message = string.Empty;
        return false;
    }

    [RequiresUnreferencedCode(
        "Member validation reflects over model properties that trimming may remove."
    )]
    [RequiresDynamicCode("Member validation inspects model properties at runtime.")]
    private static ValidationAttribute[] GetMemberValidationAttributes(
        Type modelType,
        string memberName
    ) =>
        MemberValidationAttributes.GetOrAdd(
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
    ) => ById.GetOrAdd(modelType, _ => factory(schema));
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
