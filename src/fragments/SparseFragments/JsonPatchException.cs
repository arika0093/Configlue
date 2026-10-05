using System.ComponentModel;

#if CONFIGLUE_FRAGMENT_RUNTIME
namespace Configlue;

#else
namespace SparseFragments;

#endif

/// <summary>RFC 6902 interop failure with a machine-readable kind.</summary>
[EditorBrowsable(EditorBrowsableState.Advanced)]
public sealed class JsonPatchException : Exception
{
    /// <summary>Initializes a new instance.</summary>
    public JsonPatchException(JsonPatchErrorKind kind, string message)
        : base(message)
    {
        Kind = kind;
    }

    /// <summary>Initializes a new instance with an inner exception.</summary>
    public JsonPatchException(JsonPatchErrorKind kind, string message, Exception? innerException)
        : base(message, innerException)
    {
        Kind = kind;
    }

    /// <summary>The failure kind.</summary>
    public JsonPatchErrorKind Kind { get; }
}
