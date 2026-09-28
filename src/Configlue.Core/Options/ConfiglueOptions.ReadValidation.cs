using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Configlue;

public sealed partial class ConfiglueOptions<TModel, TFragment>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private void Validate(TModel value)
    {
        var failures = new List<string>();
        CollectValidationFailures(value, failures);
        if (failures.Count > 0)
        {
            throw new ConfiglueValidationException(_optionsName, typeof(TModel), failures);
        }
    }

    private void CollectValidationFailures(TModel value, List<string> failures)
    {
        foreach (var validator in _validators)
        {
            failures.AddRange(validator.Validate(_optionsName, value));
        }

        if (
            _validateDataAnnotations
            && RuntimeFeature.IsDynamicCodeSupported
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

        CollectValidationFailures(
            TModel.FromFragment(_modelDefaultsFragment.Merge(fragment)),
            failures
        );
        failures = failures.Distinct(StringComparer.Ordinal).ToList();
        if (failures.Count == 0)
        {
            return;
        }

        _logger?.LogWarning(
            ReadValidationEvent,
            "Configuration source {SourceId} contributed invalid values for {ModelType} options {OptionsName}: {Failures}.",
            source.Id,
            typeof(TModel).FullName,
            _optionsName,
            string.Join("; ", failures)
        );
        throw new ConfiglueValidationException(
            _optionsName,
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
            "Ignoring invalid values from configuration source {SourceId} for {ModelType} options {OptionsName}: {Failures}.",
            source.Id,
            typeof(TModel).FullName,
            _optionsName,
            string.Join("; ", failures)
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
        if (!_validateDataAnnotations || !RuntimeFeature.IsDynamicCodeSupported)
        {
            return fragment;
        }

        foreach (var present in fragment.EnumeratePresentMembers())
        {
            var member = schema
                .Members.Where(candidate => candidate.Id == present.Id)
                .Cast<ConfiglueMemberSchema?>()
                .FirstOrDefault();
            if (member is not { } found)
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
                    schema.ModelType,
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
            _validateDataAnnotations && RuntimeFeature.IsDynamicCodeSupported;
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
            "Resolved configuration for {ModelType} options {OptionsName} failed validation: {Failures}.",
            typeof(TModel).FullName,
            _optionsName,
            string.Join("; ", failures)
        );
        throw new ConfiglueValidationException(_optionsName, typeof(TModel), failures);
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
            if (GetMemberValidationAttributes(schema.ModelType, member.Name).Length > 0)
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
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            return;
        }

        foreach (var present in fragment.EnumeratePresentMembers())
        {
            var member = schema
                .Members.Where(candidate => candidate.Id == present.Id)
                .Cast<ConfiglueMemberSchema?>()
                .FirstOrDefault();
            if (member is not { } found)
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
                    schema.ModelType,
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

    [RequiresUnreferencedCode(
        "Member validation reflects over model properties that trimming may remove."
    )]
    [RequiresDynamicCode("Member validation inspects model properties at runtime.")]
    private static bool CollectMemberAttributeFailures(
        Type modelType,
        string memberName,
        object? value,
        out string message
    )
    {
        var failure = GetMemberValidationAttributes(modelType, memberName)
            .Where(attribute => !attribute.IsValid(value))
            .Select(attribute => attribute.FormatErrorMessage(memberName))
            .FirstOrDefault();
        if (failure is null)
        {
            message = string.Empty;
            return false;
        }

        message = failure;
        return true;
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
}
