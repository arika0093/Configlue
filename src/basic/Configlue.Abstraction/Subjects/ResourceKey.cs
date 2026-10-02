using System.Text;

namespace Configlue;

/// <summary>A canonical, source-specific key that a provider uses to address one resource operation.</summary>
public readonly record struct ResourceKey
{
    private readonly string? _value;

    private ResourceKey(string value) => _value = value;

    /// <summary>The key for the default resource address.</summary>
    public static ResourceKey Default => default;

    /// <summary>Whether this key represents the default resource address.</summary>
    public bool IsDefault => _value is null;

    /// <summary>The canonical provider-facing value. The default key is an empty string.</summary>
    public string Value => _value ?? string.Empty;

    /// <summary>Creates a source-specific key from one provider-facing value.</summary>
    public static ResourceKey From(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return new ResourceKey(value.Normalize(NormalizationForm.FormC));
    }

    /// <summary>Explicitly uses an application's canonical subject key as this source's resource key.</summary>
    public static ResourceKey From(SubjectKey subjectKey) =>
        subjectKey.IsDefault ? Default : From(subjectKey.Value);

    /// <inheritdoc />
    public override string ToString() => Value;
}
