using System.Text;

namespace Configlue;

/// <summary>A canonical, opaque key identifying one subject's logical state.</summary>
/// <remarks>Advanced vocabulary: ordinary application code works with subject objects, not keys.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public readonly record struct SubjectKey
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string? _value;

    private SubjectKey(string value) => _value = value;

    /// <summary>The key used for server-wide state.</summary>
    public static SubjectKey Default => default;

    /// <summary>Whether this key represents server-wide state.</summary>
    public bool IsDefault => _value is null;

    /// <summary>The canonical provider-facing value. The default key is an empty string.</summary>
    public string Value => _value ?? string.Empty;

    /// <summary>Creates a key from one opaque segment.</summary>
    public static SubjectKey From(string segment)
    {
        var encoded = EncodeSegment(segment);
        return new SubjectKey(string.Concat("subject:", encoded.Length.ToString(), ":", encoded));
    }

    /// <summary>Creates a key by encoding each segment independently.</summary>
    public static SubjectKey FromSegments(params string[] segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Length == 0)
        {
            throw new ArgumentException("At least one key segment is required.", nameof(segments));
        }

        var builder = new StringBuilder("subject:");
        foreach (var segment in segments)
        {
            var encoded = EncodeSegment(segment);
            builder.Append(encoded.Length).Append(':').Append(encoded);
        }

        return new SubjectKey(builder.ToString());
    }

    private static string EncodeSegment(string segment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(segment);
        var canonicalSegment = segment.Normalize(NormalizationForm.FormC);
        return Convert
            .ToBase64String(StrictUtf8.GetBytes(canonicalSegment))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    /// <inheritdoc />
    public override string ToString() => Value;
}
